using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using FileTransform.Logging;
using Renci.SshNet;
using System.Xml;
using FileTransform.Services;

namespace FileTransform.SFTPExtract
{
    public class SFTPFileExtract : IDisposable
    {
        private SftpClient _sftpClient;
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private string _password;

        private SftpClient _fivebelowsftpClient;
        private readonly string _fbhost;
        private readonly int _fbport;
        private readonly string _fbusername;
        private string _fbpassword;

        public SFTPFileExtract(JObject clientSettings)
        {            
            // Retrieve SFTP settings for Legacy from clientSettings
            _host = clientSettings["FTPSettings"]["Host"]?.ToString() ?? string.Empty;
            _username = clientSettings["FTPSettings"]["Username"]?.ToString() ?? string.Empty;
            _port = (int)clientSettings["FTPSettings"]["Port"];

            string clientName = clientSettings["ClientName"]?.ToString() ?? string.Empty;

            if (clientName.Equals("manhattanpunch", StringComparison.OrdinalIgnoreCase))
            {
                // Retrieve SFTP settings for FiveBelow from clientSettings
                _fbhost = clientSettings["FiveBelow_FTPSettings"]["Host"]?.ToString() ?? string.Empty;
                _fbusername = clientSettings["FiveBelow_FTPSettings"]["Username"]?.ToString() ?? string.Empty;
                _fbport = (int)clientSettings["FiveBelow_FTPSettings"]["Port"];
            }

            // Retrieve the passwords asynchronously and wait for them
            Task.Run(async () =>
            {
                var passwords = await GetSFTPPasswords(clientSettings);
                _password = passwords.LegacyPassword;
                _fbpassword = passwords.FiveBelowPassword;

                InitializeSftpClients();
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Initializes the SFTP clients after retrieving the passwords.
        /// </summary>
        private void InitializeSftpClients()
        {
            if (string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_fbpassword))
            {
                throw new InvalidOperationException("SFTP password retrieval failed.");
            }

            _sftpClient = new SftpClient(_host, _port, _username, _password);
            _fivebelowsftpClient = new SftpClient(_fbhost, _fbport, _fbusername, _fbpassword);

            LoggerObserver.Info($"SFTP clients initialized: Legacy({_host}:{_port}), FiveBelow({_fbhost}:{_fbport})");
        }

        /// <summary>
        /// Retrieves both SFTP passwords from Azure Key Vault.
        /// </summary>
        private static async Task<(string LegacyPassword, string FiveBelowPassword)> GetSFTPPasswords(JObject clientSettings)
        {
            string vaultUrl = clientSettings["AzureKeyVault"]["AZURE_KEYVAULT_URL"]?.ToString() ?? string.Empty;
            string tenantId = clientSettings["AzureKeyVault"]["AZURE_KEYVAULT_TENANT_ID"]?.ToString() ?? string.Empty;
            string clientId = clientSettings["AzureKeyVault"]["AZURE_KEYVAULT_CLIENT_ID"]?.ToString() ?? string.Empty;
            string clientSecret = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_CLIENT_SECRET") ?? string.Empty;

            if (string.IsNullOrEmpty(vaultUrl) || string.IsNullOrEmpty(tenantId) ||
                string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                throw new InvalidOperationException("Azure Key Vault variables are missing.");
            }

            try
            {
                var keyVaultService = new KeyVaultService(vaultUrl, tenantId, clientId, clientSecret);

                string legacyPassword = await keyVaultService.GetSecretAsync(clientSettings["FTPSettings"]["SFTPPassword_SecretName"]?.ToString() ?? string.Empty);
                if (string.IsNullOrEmpty(legacyPassword))
                {
                    throw new InvalidOperationException("Legacy SFTP password could not be retrieved from Key Vault.");
                }

                string clientName = clientSettings["ClientName"]?.ToString() ?? string.Empty;

                if (clientName.Equals("manhattanpunch", StringComparison.OrdinalIgnoreCase))
                {
                    clientSecret = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_MANH_SFTP_SECRET") ?? string.Empty;
                    keyVaultService = new KeyVaultService(vaultUrl, tenantId, clientId, clientSecret);
                    string fiveBelowPassword = await keyVaultService.GetSecretAsync(clientSettings["FiveBelow_FTPSettings"]["FB_SFTPPassword_SecretName"]?.ToString() ?? string.Empty);
                    if (string.IsNullOrEmpty(fiveBelowPassword))
                    {
                        throw new InvalidOperationException("FiveBelow SFTP password could not be retrieved from Key Vault.");
                    }

                    return (legacyPassword, fiveBelowPassword);
                }

                return (legacyPassword, null); // Returning only the legacy password
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "Failed to retrieve SFTP passwords from Key Vault.");
                throw;
            }
        }

        public string DownloadAndExtractFile(string remoteDirectoryPath, string localDirectoryPath, string fileNamePrefix)
        {
            try
            {
                // Delete any existing EmployeeAttributeEntity files in the mapping folder
                DeleteExistingMappingFiles(localDirectoryPath);

                // Connect to SFTP
                _sftpClient.Connect();

                // Step 1: List files in the remote directory and find the latest "EmployeeAttributeEntity" .gz file
                var files = _sftpClient.ListDirectory(remoteDirectoryPath)
                                       .Where(f => f.Name.StartsWith(fileNamePrefix) && f.Name.EndsWith(".gz") && !f.IsDirectory)
                                       .OrderByDescending(f => f.LastWriteTime)
                                       .ToList();

                if (!files.Any())
                {
                    LoggerObserver.OnFileFailed("No matching .gz files found in the specified directory.");
                    return "";
                }

                // Select the latest file based on LastWriteTime
                var latestFile = files.First();
                string localFilePath = Path.Combine(localDirectoryPath, latestFile.Name);

                // Step 2: Download the latest file
                using (var fileStream = File.Create(localFilePath))
                {
                    _sftpClient.DownloadFile(latestFile.FullName, fileStream);
                }
                LoggerObserver.LogFileProcessed($"Downloaded file: {latestFile.Name} to {localFilePath}");

                // Step 3: Extract the .gz file
                string extractedFilePath = Path.Combine(localDirectoryPath, Path.GetFileNameWithoutExtension(latestFile.Name));
                ExtractGzFile(localFilePath, extractedFilePath);

                return extractedFilePath;
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "Error in SFTP file download/extraction process.");
                return "";
            }
            finally
            {
                // Ensure SFTP Client disconnects after operation
                if (_sftpClient.IsConnected)
                {
                    _sftpClient.Disconnect();
                }
            }
        }

        private void DeleteExistingMappingFiles(string mappingFilesFolderPath)
        {
            try
            {
                var filesToDelete = Directory.GetFiles(mappingFilesFolderPath, "EmployeeEntity*");

                foreach (var file in filesToDelete)
                {
                    File.Delete(file);
                    LoggerObserver.Info($"Deleted existing mapping file: {file}");
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "Error deleting old mapping files.");
            }
        }

        private void ExtractGzFile(string gzFilePath, string outputFilePath)
        {
            try
            {
                using (FileStream originalFileStream = new FileStream(gzFilePath, FileMode.Open, FileAccess.Read))
                using (GZipStream decompressionStream = new GZipStream(originalFileStream, CompressionMode.Decompress))
                using (FileStream decompressedFileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write))
                {
                    decompressionStream.CopyTo(decompressedFileStream);
                    LoggerObserver.LogFileProcessed($"File has been decompressed to: {outputFilePath}");
                }
                File.Delete(gzFilePath);
                LoggerObserver.LogFileProcessed($"Deleted the .gz file: {gzFilePath}");
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "Error extracting .gz file.");
            }
        }

        public void EnsureDirectoryExists(string directoryPath)
        {
            try
            {
                _sftpClient.Connect();
                if (!_sftpClient.Exists(directoryPath))
                {
                    _sftpClient.CreateDirectory(directoryPath);
                    LoggerObserver.Info($"Created SFTP directory: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, $"Failed to create SFTP directory: {directoryPath}");
            }
            finally
            {
                // Ensure SFTP Client disconnects after operation
                if (_sftpClient.IsConnected)
                {
                    _sftpClient.Disconnect();
                }
            }
        }

        public void Ensure_FiveBelowDirectoryExists(string directoryPath)
        {
            try
            {
                _fivebelowsftpClient.Connect();
                if (!_fivebelowsftpClient.Exists(directoryPath))
                {
                    _fivebelowsftpClient.CreateDirectory(directoryPath);
                    LoggerObserver.Info($"Created SFTP directory: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, $"Failed to create SFTP directory: {directoryPath}");
            }
            finally
            {
                // Ensure SFTP Client disconnects after operation
                if (_fivebelowsftpClient.IsConnected)
                {
                    _fivebelowsftpClient.Disconnect();
                }
            }
        }

        public void UploadXmlToSftp(XmlDocument xmlDoc, string sftpFilePath)
        {
            try
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    xmlDoc.Save(memoryStream);
                    memoryStream.Position = 0;
                    _fivebelowsftpClient.Connect();
                    _fivebelowsftpClient.UploadFile(memoryStream, sftpFilePath);
                }
                LoggerObserver.Info($"Successfully uploaded file: {sftpFilePath}");
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, $"Failed to upload XML to SFTP: {sftpFilePath}");
            }
            finally
            {
                // Ensure SFTP Client disconnects after operation
                if (_fivebelowsftpClient.IsConnected)
                {
                    _fivebelowsftpClient.Disconnect();
                }
            }
        }
        public void Dispose()
        {
            if (_sftpClient != null)
            {
                if (_sftpClient.IsConnected)
                    _sftpClient.Disconnect();

                _sftpClient.Dispose();
            }

            if (_fivebelowsftpClient != null)
            {
                if (_fivebelowsftpClient.IsConnected)
                    _fivebelowsftpClient.Disconnect();

                _fivebelowsftpClient.Dispose();
            }
        }
    }
}
