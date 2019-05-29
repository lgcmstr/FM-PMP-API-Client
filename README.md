# FM-PMP-API-Client: Password Manager Pro API Client

## Business Background

Most if not all GSA web applications and backend jobs require configuration information that needs to be kept secure. The most common of these are database credentials, but can also include other account information as well as decryption keys. Storing this information in plain-text configuration files could lead to unintended exposure. It also increases complexity when the information must be updated, such as during regular password change events. Many files may need to be updated, and any missed files could lead to application error and downtime.

GSA uses the __Password Manager Pro__ (PMP) application to securely store user passwords in a central repository. PMP provides a _RESTful_ API which allows for secure HTTP requests to retrieve passwords. This API is ideal for web applications and backend jobs to retrieve passwords on-demand, eliminating the need to store them in configuration files. It also adds the benefit of requiring an update to only one location (in PMP). This reduces complexity and improves uptime.

## Technology Background

The __FM-PMP-API-Client__ project is a _.NET Core_ DLL. It is designed to be added as a project reference into other _.NET Core_ projects. Once added as a reference, the API client should be configured as a singleton (usually in _Startup.cs_). Then the client can be used via _dependency injection_ in any class that requires password retrieval.

**NOTE**: This project will eventually be available as a NuGet package.

## Setup and Installation

### <a name="configuration"></a>Configuration

The __PMPAPIClient_config.json__ configuration file is required. The required keys/values are:
* _AuthToken_ : The PMP API authorization token for the server you will be running your parent project from. Requests to PMP's RESTful API are protected via server/IP white-listing. You will need to work with the organization's PMP administrator to request a key for the server you are connecting from.
* _Host_ : The fully-qualified domain name (FQDN) and optional port for the server hosting the PMP RESTful API.
* _Prefix_ : Any prefix mandated by the PMP administrator to be used for all keys that can be accessed through the RESTful API. This is currently set to 'API', and is unlikely to change.

### <a name="deploymentPrerequisites"></a>Deployment Prerequisites

1. Create a folder on your server _outside_ of your parent application root.
2. Ensure that the _JSON_ files detailed in the [Configuration](#configuration) section are in this folder. Example files are located in this source repository.
3. Create a server environment variable named **APPSETTINGS_DIRECTORY** that maps to this new folder. This environment variable can be set in Visual Studio project settings, or as a server environment variable if running _dotnet_ from the command-line.

### Development Environment

In Visual Studio for your parent project, go to _Project->Properties->Debug_. Create/edit the **APPSETTINGS_DIRECTORY** environment variable to point to the _appsettings_ folder you created [above](#deploymentPrerequisites).

## Usage

### Startup

Your application startup routine is where you set up the PMP API Client as a singleton and configure logging. The following examples are for a web application using the _Startup.cs_ file.

First, in _ConfigureServices_, you add _IPMPAPIClientService_ as a named HTTP client. Then you add it as a singleton, passing in instances of _ILogger_ and _IHttpClientFactory_.

**NOTE**: As shown, the call to _ConfigurePrimaryHttpMessageHandler_ is meant only for development environments where the PMP API host is using an unverified server certificate. This example forces those certificate validation checks to always return _true_. Test and production environments should have fully-verified certificates.

    public void ConfigureServices(IServiceCollection services)
    {
        // Initialize the Password Manager Pro Client API as a singleton.
        services.AddHttpClient<IPMPAPIClientService>("PMPAPIClientService").ConfigurePrimaryHttpMessageHandler(() => {
            var handler = new HttpClientHandler();
            if (_hostingEnvironment.IsDevelopment())
            {
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => { return true; };
            }
            return handler;
        });
        services.AddSingleton<IPMPAPIClientService>(client => new PMPAPIClientService(client.GetService<ILogger<PMPAPIClientService>>(),
                client.GetService<IHttpClientFactory>()));

Now you're all set to resolve passwords via dependency injection in any of your application's classes. If you happen to require password resolution within _ConfigureServices_ in _Startup.cs_, such as when registering a database context for dependency injection, follow this example:

    // Set up our PostgreSQL context for dependency injections. We get the connection string
    // from the PMP API Client, so we build a service provider to access the singleton we
    // configured above.
    var connectionString = "";
    var sp = services.BuildServiceProvider();
    var pmpAPIClientService = sp.GetService<IPMPAPIClientService>();
    var pmpKey = Configuration["ConnectionStrings:Recon:PMPKey"];
    if (!string.IsNullOrEmpty(pmpKey))
    {
        // Retrieve it.
        connectionString = pmpAPIClientService.RetrievePassword(pmpKey).Result;
    }
    // Fall back to the connection string in the config file, if available.
    if (string.IsNullOrEmpty(connectionString))
    {
        connectionString = Configuration["ConnectionStrings:Recon:ConnectionString"];
    }
    services.AddDbContext<reconContext>(opts => opts.UseNpgsql(connectionString));

Notice the call to _pmpAPIClientService.RetrievePassword().Result_. That's all you need to do to retrieve a password!

### Dependency Injection

To resolve passwords within a class, first inject the _IPMPAPIClientService_ during instantiation as follows:

    public class LoginController : Controller
    {
        private readonly reconContext _context;
        private readonly IHostingEnvironment _env;
        private readonly IStringLocalizer<LoginController> _localizer;
        private readonly IConfiguration _configuration;
        private readonly IPMPAPIClientService _pmpAPIClientService;

        public LoginController(reconContext context, IHostingEnvironment env,
            IStringLocalizer<LoginController> localizer, IConfiguration configuration,
            IPMPAPIClientService pmpAPIClientService)
        {
            _context = context;
            _env = env;
            _localizer = localizer;
            _configuration = configuration;
            _pmpAPIClientService = pmpAPIClientService;
        }

Then you can resolve passwords within any class method via a call to _RetrievePassword()_. The following example shows how _SecureAuth_ keys that are stored in PMP are being retrieved:

    // Decrypt the token with our SecureAuth keys. Get them from PMP!
    var validationKey = "";
    var decryptionKey = "";
    var pmpKey = _configuration["SecureAuth:ValidationKey:PMPKey"];
    if (!string.IsNullOrEmpty(pmpKey))
    {
        // Retrieve it.
        validationKey = _pmpAPIClientService.RetrievePassword(pmpKey).Result;
    }
    // Fall back to the value in the config file, if available.
    if (string.IsNullOrEmpty(validationKey))
    {
        validationKey = _configuration["SecureAuth:ValidationKey:Failover"];
    }
    pmpKey = _configuration["SecureAuth:DecryptionKey:PMPKey"];
    if (!string.IsNullOrEmpty(pmpKey))
    {
        // Retrieve it.
        decryptionKey = _pmpAPIClientService.RetrievePassword(pmpKey).Result;
    }
    // Fall back to the value in the config file, if available.
    if (string.IsNullOrEmpty(decryptionKey))
    {
        decryptionKey = _configuration["SecureAuth:DecryptionKey:Failover"];
    }

### Failures

All password retrieval failures will return an empty string ("") and detailed error information will be sent to the log. It is up to your calling project to gracefully handle a failed retrieval. Inspect the log to determine what went wrong.