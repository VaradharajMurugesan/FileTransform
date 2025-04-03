using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FileTransform.Logging;
using Newtonsoft.Json.Linq;

namespace FileTransform.Services
{
    public class KeyVaultService
    {
        private readonly SecretClient _client;

        public KeyVaultService(string vaultUrl, string tenantId, string clientId, string clientSecret)
        {
            _client = new SecretClient(
                new Uri(vaultUrl),
                new ClientSecretCredential(tenantId, clientId, clientSecret)
            );
        }

        /// <summary>
        /// Retrieves secrets from Azure Key Vault and constructs the connection string.
        /// </summary>
        public async Task<string> GetConnectionStringAsync(JObject clientSettings)
        {
            try
            {
                string hostname = clientSettings["SQLConnection"]["HostName"]?.ToString() ?? string.Empty;
                string username = clientSettings["SQLConnection"]["UserName"]?.ToString() ?? string.Empty;
                string password = await GetSecretAsync(clientSettings["SQLConnection"]["DBPassword_SecretName"]?.ToString() ?? string.Empty);
                string database = clientSettings["SQLConnection"]["Database"]?.ToString() ?? string.Empty;
                string dbConnection = string.Empty;

                if (!string.IsNullOrEmpty(hostname) &&
                    !string.IsNullOrEmpty(username) &&
                    !string.IsNullOrEmpty(password) &&
                    !string.IsNullOrEmpty(database))
                {
                    dbConnection = $"Server={hostname};Database={database};User Id={username};Password={password};";
                    return dbConnection;
                }
                else
                {
                    var ex = new InvalidOperationException("One or more secrets could not be retrieved.");
                    LoggerObserver.Error(ex, "One or more secrets could not be retrieved.");
                    throw ex;
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, ex.Message);
                throw;  // Preserve stack trace
            }
        }

        /// <summary>
        /// Retrieves the SFTP password from Azure Key Vault.
        /// </summary>
        public async Task<string> GetSFTPPasswordAsync(JObject clientSettings)
        {
            try
            {
                // Use the corrected GetSecretAsync method
                string password = await GetSecretAsync(clientSettings["FTPSettings"]["SFTPPassword_SecretName"]?.ToString() ?? string.Empty);

                if (!string.IsNullOrEmpty(password))
                {
                    return password;
                }
                else
                {
                    var ex = new InvalidOperationException("Password is not valid for SFTP connection.");
                    LoggerObserver.Error(ex, "Password is empty or not fetched properly from the Key Vault.");
                    throw ex;
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, ex.Message);
                throw;  // Preserve stack trace
            }
        }

        /// <summary>
        /// Retrieves a secret value from Azure Key Vault.
        /// </summary>
        public async Task<string> GetSecretAsync(string secretName)
        {
            try
            {
                // Retrieve the secret response
                Response<KeyVaultSecret> secretResponse = await _client.GetSecretAsync(secretName);

                // Extract the actual secret value
                string secretValue = secretResponse?.Value?.Value;

                if (string.IsNullOrEmpty(secretValue))
                {
                    throw new InvalidOperationException($"Secret '{secretName}' is empty or could not be retrieved.");
                }

                return secretValue;
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, $"Failed to retrieve secret {secretName} from Key Vault.");
                throw;
            }
        }
    }
}
