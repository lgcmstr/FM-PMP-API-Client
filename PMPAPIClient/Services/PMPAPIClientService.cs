using GSA.PMPAPIClient.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace GSA.PMPAPIClient.Services
{
    public class PMPAPIClientService : IPMPAPIClientService
    {
        private readonly ILogger<PMPAPIClientService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ConcurrentDictionary<string, AccountInfo> _accounts;
        private readonly ConcurrentDictionary<string, PasswordInfo> _passwords;
        private readonly string _configFileName = "PMPAPIClient_config.json";
        private readonly string _endpointPrefix = "/restapi/json/v1";
        private ConfigurationData _configurationData;
        private DateTime? _lastConfigRead;

        public PMPAPIClientService(ILogger<PMPAPIClientService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _accounts = new ConcurrentDictionary<string, AccountInfo>();
            _passwords = new ConcurrentDictionary<string, PasswordInfo>();
            _lastConfigRead = null;
        }

        private bool CheckConfigurationFile()
        {
            // Config file path is in the environment variable.
            var configPath = Environment.GetEnvironmentVariable("APPSETTINGS_DIRECTORY");
            if (configPath == null)
            {
                ResetAndLogErrorMessage("Environment variable not set.");
                return false;
            }

            // Does the config file exist?
            var configFilePath = Path.Combine(configPath, _configFileName);
            if (!File.Exists(configFilePath))
            {
                ResetAndLogErrorMessage(String.Format("{0} file not found.", _configFileName));
                return false;
            }

            try
            {
                // Has our config file been updated since the last check?
                if (_lastConfigRead == null ||
                    DateTime.Compare(File.GetLastWriteTime(configFilePath), (DateTime)_lastConfigRead) > 0)
                {
                    // Parse the config file.
                    return ParseConfigurationFile(configFilePath);
                }
            }
            catch (Exception ex)
            {
                ResetAndLogErrorMessage(String.Format("Failed to parse {0}: {1}", _configFileName, ex.Message));
                return false;
            }

            // We fall through here if the config file hasn't been updated since our last read.
            return true;
        }

        private bool ParseConfigurationFile(string filePath)
        {
            _logger.LogInformation(LogMessage("ParseConfigurationFile called."));

            // Ensure the config file has what we expect.
            List<string> errors = new List<string>();
            _configurationData = JsonConvert.DeserializeObject<ConfigurationData>(File.ReadAllText(filePath),
                new JsonSerializerSettings
                {
                    Error = delegate(object sender, Newtonsoft.Json.Serialization.ErrorEventArgs args)
                    {
                        errors.Add(args.ErrorContext.Error.Message);
                        args.ErrorContext.Handled = true;
                    }
                });

            if (errors.Count > 0)
            {
                ResetAndLogErrorMessage(String.Format("Failed to parse {0}", _configFileName));
                foreach (var error in errors) _logger.LogError(LogMessage(error));
                return false;
            }

            _lastConfigRead = DateTime.Now;

            // Now retrieve all "Application" resources and accounts available to us.
            return RetrieveResourcesAndAccounts().Result;
        }

        public Task<string> RetrievePassword(string key)
        {
            var password = "";
            _logger.LogInformation(LogMessage(String.Format("RetrievePassword called. Key: {0}", key)));

            // We always check the config file to see if there was an update. That will affect whether
            // we attempt to re-retrieve the password.
            if (CheckConfigurationFile())
            {
                // Add the API prefix to the key name.
                var accountName = String.Format("{0}{1}", _configurationData.Prefix, key);

                // Did this "account" name exist when we last reloaded?
                if (_accounts.TryGetValue(accountName, out AccountInfo info))
                {
                    PasswordInfo passwordInfo = null;
                    // Have we already fetched this password since the last config update?
                    if (_passwords.TryGetValue(accountName, out passwordInfo)) { }

                    if (passwordInfo == null || DateTime.Compare((DateTime)_lastConfigRead, passwordInfo.LastRead) > 0)
                    {
                        // Retrieve the password.
                        _logger.LogInformation(LogMessage("RetrievePassword calling API."));

                        var endpoint = String.Format("/resources/{0}/accounts/{1}/password",
                            info.GroupId, info.AccountId);
                        var resource = CallPMPAPIEndpoint(endpoint).Result;
                        var operation = (JObject)resource["operation"];
                        if (operation != null)
                        {
                            var details = (JObject)operation["Details"];
                            if (details != null)
                            {
                                password = (string)details["PASSWORD"];

                                if (password != null)
                                {
                                    // We got it!
                                    if (passwordInfo == null)
                                    {
                                        passwordInfo = new PasswordInfo
                                        {
                                            Password = password,
                                            LastRead = DateTime.Now
                                        };
                                    }
                                    else
                                    {
                                        // Update it.
                                        passwordInfo.Password = password;
                                        passwordInfo.LastRead = DateTime.Now;
                                    }

                                    _passwords.AddOrUpdate(accountName, passwordInfo,
                                    (name, existingVal) =>
                                    {
                                        // Just in case, always overwrite with the new information.
                                        return passwordInfo;
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        // Just use the cached value.
                        _logger.LogInformation(LogMessage("RetrievePassword using cached value."));
                        password = passwordInfo.Password;
                    }
                }
                else
                {
                    // This key/account name didn't exist when we enumerated after the last config update.
                    // We don't force a config update in this case, as it would result in excess
                    // load on the PMP API. It's up to admins to fix the key issue and then manually update
                    // the config file timestamp so an update will occur.
                    _logger.LogError(LogMessage(String.Format("RetrievePassword could not find key: {0}", key)));
                }
            }

            return Task.FromResult(password);
        }

        private Task<bool> RetrieveResourcesAndAccounts()
        {
            _accounts.Clear();

            // Grab our available "Application" resources.
            var resources = CallPMPAPIEndpoint("/resources").Result;
            var operation = (JObject)resources["operation"];
            if (operation != null)
            {
                var details = (JArray)operation["Details"];
                if (details != null)
                {
                    foreach (JObject resource in details)
                    {
                        var type = (string)resource["RESOURCE TYPE"];

                        if (type == "Application")
                        {
                            // We found a group we can dive into.
                            var id = (string)resource["RESOURCE ID"];

                            if (id != null) RetrieveAccounts(id).Wait();
                        }
                    }
                }
            }

            // Success if we added at least one account.
            return Task.FromResult(_accounts.Count > 0);
        }

        private Task<bool> RetrieveAccounts(string resourceId)
        {
            var result = false;

            var endpoint = String.Format("/resources/{0}/accounts", resourceId);
            var resource = CallPMPAPIEndpoint(endpoint).Result;
            var operation = (JObject)resource["operation"];
            if (operation != null)
            {
                var details = (JObject)operation["Details"];
                if (details != null)
                {
                    var accounts = (JArray)details["ACCOUNT LIST"];
                    if (accounts != null)
                    {
                        foreach (JObject account in accounts)
                        {
                            var id = (string)account["ACCOUNT ID"];
                            var name = (string)account["ACCOUNT NAME"];

                            if (id != null && name != null)
                            {
                                // "Success" if we found at least one account.
                                result = true;

                                var info = new AccountInfo
                                {
                                    GroupId = resourceId,
                                    AccountId = id
                                };

                                _accounts.AddOrUpdate(name, info,
                                (key, existingVal) =>
                                {
                                    // Just in case, always overwrite with the new ids.
                                    return info;
                                });
                            }
                        }
                    }
                }
            }

            return Task.FromResult(result);
        }

        private Task<JObject> CallPMPAPIEndpoint(string endpoint)
        {
            var url = String.Format("https://{0}{1}{2}?AUTHTOKEN={3}", _configurationData.Host, _endpointPrefix,
                endpoint, _configurationData.AuthToken);
            
            try
            {
                var client = _httpClientFactory.CreateClient("PMPAPIClientService");
                var result = client.GetStringAsync(url).Result;
                var jsonObject = JObject.Parse(result);
                return Task.FromResult(jsonObject);
            }
            catch (Exception ex)
            {
                // Log the error and return an empty JSON object.
                ResetAndLogErrorMessage(String.Format("Endpoint call failed: {0}: {1}", url, ex.Message));
                return Task.FromResult(new JObject());
            }
        }
        
        // Helper routine to log and error and reset our state so we'll try again on the next attempt.
        private void ResetAndLogErrorMessage(string message)
        {
            _logger.LogError(LogMessage(message));
            _lastConfigRead = null;
        }

        private string LogMessage(string message)
        {
            return String.Format("{0}", message);
        }

        public class ConfigurationData
        {
            [JsonProperty(Required = Required.Always)]
            public string AuthToken { get; set; }
            [JsonProperty(Required = Required.Always)]
            public string Host { get; set; }
            [JsonProperty(Required = Required.Always)]
            public string Prefix { get; set; }
        }

        private class AccountInfo
        {
            public string GroupId { get; set; }
            public string AccountId { get; set; }
        }

        private class PasswordInfo
        {
            public string Password { get; set; }
            public DateTime LastRead { get; set; }
        }
    }
}
