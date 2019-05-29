using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace GSA.PMPAPIClient.Interfaces
{
    public interface IPMPAPIClientService
    {
        Task<string> RetrievePassword(string key);
    }
}
