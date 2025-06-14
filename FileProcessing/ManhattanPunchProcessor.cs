﻿using CsvHelper;
using CsvHelper.Configuration;
using Newtonsoft.Json.Linq;
using FileTransform_Manhattan.DataModel;
using FileTransform_Manhattan.Helpers;
using FileTransform_Manhattan.Logging;
using FileTransform_Manhattan.SFTPExtract;
using FileTransform_Manhattan.Services;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Dapper;
using static System.Net.WebRequestMethods;
using System.IO;
using System.Configuration;
using Renci.SshNet;
using TimeZoneConverter;


namespace FileTransform_Manhattan.FileProcessing
{
    internal class ManhattanPunchProcessor : ICsvFileProcessorStrategy
    {
        private string _connectionString = string.Empty;
        // Grouped HR mapping: Dictionary maps employeeId -> EmployeeHrData
        private Dictionary<int, ManhattanLocationData> LocationMapping;
        private Dictionary<string, LocationEntityData> TimeZoneMapping;
        private bool mealBreakFlag = false;
        private string outputFileName = string.Empty;
        private string sftpOutPutFolderPath = string.Empty;
        private int fetchRecordsOlderThanDays = 0;
        // Step 1: Retrieve last used INSERT_BATCH_ID and increment it
        private int insertBatchId = 1; // Default starting value
        private SFTPFileExtract _sftpFileExtract = null;  // Make it a private readonly field
        ExtractLocationEntityData extractLocation = new ExtractLocationEntityData();

        private ManhattanPunchProcessor(JObject clientSettings)
        {
            mealBreakFlag = bool.TryParse(clientSettings["Flags"]["MealBreakRequired"]?.ToString(), out bool flag) && flag;

            fetchRecordsOlderThanDays = int.TryParse(clientSettings["Flags"]["DataRetrievalDaysOffset"]?.ToString(), out int days)
                ? days
                : 14;
        }

        public static async Task<ManhattanPunchProcessor> CreateAsync(JObject clientSettings)
        {
            var processor = new ManhattanPunchProcessor(clientSettings);
            await processor.GetDBPasswordFromKeyVault(clientSettings);
            return processor;
        }

        public async Task GetDBPasswordFromKeyVault(JObject clientSettings)
        {
            string vaultUrl = clientSettings["AzureKeyVault"]["AZURE_KEYVAULT_URL"]?.ToString() ?? string.Empty;
            string tenantId = clientSettings["AzureKeyVault"]["AZURE_KEYVAULT_TENANT_ID"]?.ToString() ?? string.Empty;
            string clientId = clientSettings["AzureKeyVault"]["AZURE_KEYVAULT_CLIENT_ID"]?.ToString() ?? string.Empty;
            string clientSecret = Environment.GetEnvironmentVariable("AZURE_KEYVAULT_DB_CLIENT_SECRET") ?? string.Empty;
            if (string.IsNullOrEmpty(vaultUrl) ||
                string.IsNullOrEmpty(tenantId) ||
                string.IsNullOrEmpty(clientId) ||
                string.IsNullOrEmpty(clientSecret))
            {
                var ex = new InvalidOperationException("Azure Key vault variables are missing. Please set them.");
                LoggerObserver.Error(ex, ex.Message);
                throw ex;
            }

            try
            {
                // Use the KeyVaultService in your code
                var keyVaultService = new KeyVaultService(vaultUrl, tenantId, clientId, clientSecret);
                var connectionString = await keyVaultService.GetConnectionStringAsync(clientSettings);
                if (connectionString != null)
                {
                    _connectionString = connectionString.ToString();
                }
                else
                {
                    var ex = new InvalidOperationException("Database Connection string is not Valid.");
                    LoggerObserver.Error(ex, "Connection string is either empty or not formed properly");
                    throw ex;
                }
                string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
                sftpOutPutFolderPath = clientSettings["Folders"]["sftpOutPutFolderPath"]?.ToString() ?? string.Empty;
                outputFileName = clientSettings["Folders"]["OutputFileFormat"]?.ToString() ?? string.Empty;
                LocationMapping = LoadLocationMapping();
                string remoteMappingFilePath = clientSettings["Folders"]["remoteLocationEntityPath"]?.ToString() ?? string.Empty;
                // 🔹 Initialize the SFTPFileExtract instance here
                _sftpFileExtract = new SFTPFileExtract(clientSettings);
                string LocationEntityMappingPath = _sftpFileExtract.DownloadAndExtractFile(remoteMappingFilePath, mappingFilesFolderPath, "LocationEntity");
                TimeZoneMapping = extractLocation.LoadGroupedLocationMappingFromCsv(LocationEntityMappingPath);

            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, ex.Message);
            }

        }

        public Dictionary<int, ManhattanLocationData> LoadLocationMapping()
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();

            string query = "SELECT location_id, warehouse_id, shipcenter_location FROM warehouse_location WHERE is_enabled = 1"; // Adjust as needed

            // Use Dapper to execute the query and map the results
            var locations = connection.Query<ManhattanLocationData>(query);

            // Convert the list to a dictionary
            return locations.ToDictionary(loc => loc.location_id);
        }


        public void GenerateManhattanPunchXML(IEnumerable<ShiftGroup> groupedTimeClockData)
        {
            try
            {
                // Group records by ManhattanWarehouseId first
                var warehouseGroups = groupedTimeClockData.GroupBy(g => g.Records.First().ManhattanWarehouseId);

                foreach (var warehouseGroup in warehouseGroups)
                {
                    try
                    {
                        string warehouseId = warehouseGroup.Key;

                        // Step 1: Find the matching `location_id` for this `warehouse_id`
                        var locationEntry = LocationMapping.Values.FirstOrDefault(loc => loc.warehouse_id == warehouseId);

                        if (locationEntry == null)
                        {
                            LoggerObserver.Info($"Skipping WarehouseID {warehouseId} as no location_id is mapped.");
                            continue;
                        }
                        // Fetch shipcenter location for this warehouse ID
                        if (!LocationMapping.TryGetValue(locationEntry.location_id, out ManhattanLocationData locationData))
                        {
                            LoggerObserver.Info($"Skipping WarehouseID {warehouseGroup.Key} as no shipcenter location is mapped.");
                            continue;
                        }

                        string shipcenterFolder = locationData.shipcenter_location;

                        // Construct full SFTP subfolder path
                        string sftpSubfolderPath = $"{sftpOutPutFolderPath}/{shipcenterFolder}";

                        // Ensure the subfolder exists
                        _sftpFileExtract.Ensure_FiveBelowDirectoryExists(sftpSubfolderPath);

                        // Create a new XML Document for each warehouse
                        XmlDocument xmlDoc = new XmlDocument();
                        XmlElement root = xmlDoc.CreateElement("tXML");
                        xmlDoc.AppendChild(root);

                        // Add XML declaration with standalone="yes"
                        XmlDeclaration xmlDeclaration = xmlDoc.CreateXmlDeclaration("1.0", "UTF-8", "yes");
                        xmlDoc.InsertBefore(xmlDeclaration, root);

                        // Add header elements
                        AddHeaderElements(xmlDoc, root);

                        XmlElement message = xmlDoc.CreateElement("Message");
                        root.AppendChild(message);
                        XmlElement timeAndAttendance = xmlDoc.CreateElement("TimeAndAttendance");
                        message.AppendChild(timeAndAttendance);

                        int tranNumber = 1;

                        // Process each employee's shift group within the warehouse
                        foreach (var shiftGroup in warehouseGroup)
                        {
                            try
                            {
                                if (shiftGroup.Records.Any(r => r.EventType == "Create"))
                                {
                                    ProcessCreateEvent(xmlDoc, timeAndAttendance, ref tranNumber, shiftGroup);
                                }
                                else if (shiftGroup.Records.Any(r => r.EventType == "Delete"))
                                {
                                    ProcessDeleteEvent(xmlDoc, timeAndAttendance, ref tranNumber, shiftGroup);
                                }
                                else if (shiftGroup.Records.Any(r => r.EventType == "Delete_Create"))
                                {
                                    ProcessDeleteCreateEvent(xmlDoc, timeAndAttendance, ref tranNumber, shiftGroup);
                                }
                            }
                            catch (Exception ex)
                            {
                                LoggerObserver.Error(ex, $"Error processing shift group for WarehouseID: {warehouseGroup.Key}");
                            }
                        }

                        // Generate file name based on ManhattanWarehouseId
                        string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                        string fileName = string.Format(outputFileName, warehouseGroup.Key, timestamp, insertBatchId.ToString("D5"));
                        string sftpFilePath = $"{sftpSubfolderPath}/{fileName}";

                        // Upload the XML document to SFTP
                        _sftpFileExtract.UploadXmlToSftp(xmlDoc, sftpFilePath);

                        LoggerObserver.LogFileProcessed($"Uploaded XML file to SFTP: {sftpFilePath}");
                    }
                    catch (Exception ex)
                    {
                        LoggerObserver.Error(ex, $"Error generating XML for WarehouseID: {warehouseGroup.Key}");
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "An unexpected error occurred while generating Manhattan Punch XML.");
            }
        }



        // Helper method to add header elements
        private void AddHeaderElements(XmlDocument xmlDoc, XmlElement root)
        {
            XmlElement header = xmlDoc.CreateElement("Header");
            root.AppendChild(header);

            header.AppendChild(CreateElement(xmlDoc, "Source", "Host"));
            header.AppendChild(CreateElement(xmlDoc, "Batch_ID", "BT" + insertBatchId.ToString("D5")));
            header.AppendChild(CreateElement(xmlDoc, "Message_Type", "TAS"));
            header.AppendChild(CreateElement(xmlDoc, "Company_ID", "01"));
            header.AppendChild(CreateElement(xmlDoc, "Msg_Locale", "English (United States)"));
        }

        // Helper method to process the "Create" event
        private void ProcessCreateEvent(XmlDocument xmlDoc, XmlElement timeAndAttendance, ref int tranNumber, ShiftGroup group)
        {
            XmlElement tasData = xmlDoc.CreateElement("TASData");
            timeAndAttendance.AppendChild(tasData);
            XmlElement mergeRange = xmlDoc.CreateElement("MergeRange");
            tasData.AppendChild(mergeRange);

            mergeRange.AppendChild(CreateElement(xmlDoc, "TranNumber", tranNumber.ToString("D9")));
            mergeRange.AppendChild(CreateElement(xmlDoc, "Warehouse", group.Records.First().ManhattanWarehouseId));
            mergeRange.AppendChild(CreateElement(xmlDoc, "EmployeeUserId", group.Records.First().EmployeeExternalId.ToString()));

            DateTime? empClockIn = GetClockInTime(group, "ShiftBegin");
            DateTime? empClockOut = GetClockOutTime(group, "ShiftEnd");
            DateTime? breakClockIn = GetBreakClockInTime(group, "MealBreakBegin");
            DateTime? breakClockOut = GetBreakClockOutTime(group, "MealBreakEnd");

            DateTime? startDateForMerge = empClockIn?.AddHours(-2) ?? empClockOut?.AddHours(-2);
            DateTime? endDateForMerge = empClockOut?.AddHours(2) ?? empClockIn?.AddHours(2);

            mergeRange.AppendChild(CreateElement(xmlDoc, "StartDateForMerge", startDateForMerge?.ToString("MM/dd/yyyy HH:mm:ss")));
            mergeRange.AppendChild(CreateElement(xmlDoc, "EndDateForMerge", endDateForMerge?.ToString("MM/dd/yyyy HH:mm:ss")));

            XmlElement mergeClockInClockOut = xmlDoc.CreateElement("MergeClockInClockOut");
            mergeRange.AppendChild(mergeClockInClockOut);

            AppendClockInOutTimes(xmlDoc, mergeClockInClockOut, empClockIn, empClockOut);

            if (mealBreakFlag && (breakClockIn.HasValue || breakClockOut.HasValue))
            {
                AddBreakTimes(xmlDoc, mergeRange, breakClockIn, breakClockOut);
            }

            tranNumber++;
        }
        // Helper method to process the "Delete" event
        private void ProcessDeleteCreateEvent(XmlDocument xmlDoc, XmlElement timeAndAttendance, ref int tranNumber, ShiftGroup group)
        {
            XmlElement tasData = xmlDoc.CreateElement("TASData");
            timeAndAttendance.AppendChild(tasData);
            XmlElement deleteClockInRange = xmlDoc.CreateElement("DeleteClockInRange");
            tasData.AppendChild(deleteClockInRange);

            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "TranNumber", tranNumber.ToString("D9")));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "Warehouse", group.Records.First().ManhattanWarehouseId));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EmployeeUserId", group.Records.First().EmployeeExternalId.ToString()));

            DateTime? startDateForDel = group.Records.Min(r => r.ClockTimeAfterChange);
            DateTime? endDateForDel = group.Records.Max(r => r.ClockTimeAfterChange);

            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "StartDateForDel", startDateForDel?.ToString("MM/dd/yyyy HH:mm:ss")));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EndDateForDel", endDateForDel?.ToString("MM/dd/yyyy HH:mm:ss")));

            tranNumber++;
        }

        // Helper method to process the "Delete" event
        private void ProcessDeleteEvent(XmlDocument xmlDoc, XmlElement timeAndAttendance, ref int tranNumber, ShiftGroup group)
        {
            XmlElement tasData = xmlDoc.CreateElement("TASData");
            timeAndAttendance.AppendChild(tasData);
            XmlElement deleteClockInRange = xmlDoc.CreateElement("DeleteClockInRange");
            tasData.AppendChild(deleteClockInRange);

            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "TranNumber", tranNumber.ToString("D9")));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "Warehouse", group.Records.First().ManhattanWarehouseId));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EmployeeUserId", group.Records.First().EmployeeExternalId.ToString()));

            DateTime? startDateForDel = group.Records.Min(r => r.ClockTimeBeforeChange);
            DateTime? endDateForDel = group.Records.Max(r => r.ClockTimeBeforeChange);

            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "StartDateForDel", startDateForDel?.ToString("MM/dd/yyyy HH:mm:ss")));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EndDateForDel", endDateForDel?.ToString("MM/dd/yyyy HH:mm:ss")));

            tranNumber++;
        }

        // Helper method to get clock-in time (either from "ApproveReject" or the earliest "ShiftBegin" time)
        private DateTime? GetClockInTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Min(r => r.ClockTimeAfterChange);
        }

        // Helper method to get clock-out time (either from "ApproveReject" or the latest "ShiftEnd" time)
        private DateTime? GetClockOutTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Max(r => r.ClockTimeAfterChange);
        }

        // Helper method to get break time (either from "ApproveReject" or the earliest "MealBreakBegin" or latest "MealBreakEnd")
        private DateTime? GetBreakClockInTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Min(r => r.ClockTimeAfterChange);
        }

        // Helper method to get break time (either from "ApproveReject" or the earliest "MealBreakBegin" or latest "MealBreakEnd")
        private DateTime? GetBreakClockOutTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Max(r => r.ClockTimeAfterChange);
        }

        // Helper method to append clock-in and clock-out times
        private void AppendClockInOutTimes(XmlDocument xmlDoc, XmlElement mergeClockInClockOut, DateTime? empClockIn, DateTime? empClockOut)
        {
            if (empClockIn.HasValue)
            {
                mergeClockInClockOut.AppendChild(CreateElement(xmlDoc, "EmpClockIn", empClockIn.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }

            if (empClockOut.HasValue)
            {
                mergeClockInClockOut.AppendChild(CreateElement(xmlDoc, "EmpClockOut", empClockOut.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }
        }

        // Helper method to add break times to the XML
        private void AddBreakTimes(XmlDocument xmlDoc, XmlElement mergeRange, DateTime? breakClockIn, DateTime? breakClockOut)
        {
            XmlElement mergeBreakStartBreakEnd = xmlDoc.CreateElement("MergeBreakStartBreakEnd");
            mergeRange.AppendChild(mergeBreakStartBreakEnd);

            if (breakClockIn.HasValue)
            {
                mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "BreakStartTime", breakClockIn.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }

            if (breakClockOut.HasValue)
            {
                mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "BreakEndTime", breakClockOut.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }

            mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "Activity", "UNPAIDBRK"));
        }

        // Helper method to create XML elements
        private XmlElement CreateElement(XmlDocument xmlDoc, string name, string value)
        {
            XmlElement element = xmlDoc.CreateElement(name);
            element.InnerText = value;
            return element;
        }

        // Function to split records into groups based on a time gap
        static IEnumerable<ShiftGroup> SplitByTimeGap(IEnumerable<ClockRecord> records, TimeSpan maxGap)
        {
            var groups = new List<ShiftGroup>();
            ShiftGroup currentGroup = null;

            foreach (var record in records)
            {
                // Check if a new group needs to be created
                if (currentGroup == null || (record.ClockTimeAfterChange - currentGroup.Records[0].ClockTimeAfterChange) > maxGap)
                {
                    // Start a new group if no group exists or time gap exceeds threshold
                    currentGroup = new ShiftGroup
                    {
                        EmployeeExternalId = record.EmployeeExternalId
                    };
                    groups.Add(currentGroup);
                }

                // Add the current record to the group
                currentGroup.Records.Add(record);
            }

            return groups;
        }


        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            DateTime startTime = DateTime.Now;
            string timestamp = startTime.ToString("yyyyMMddHHmmss");
            LoggerObserver.LogFileProcessed($"Start processing Manhattanpunch CSV: {filePath} at {startTime}");

            try
            {
                // Validate if the source file exists
                if (!System.IO.File.Exists(filePath))
                {
                    throw new FileNotFoundException($"The file does not exist: {filePath}");
                }

                ReadClockRecordsFromFileAndInsertToDatabase(filePath);

                // Read and process CSV records lazily and asynchronously
                var groupedRecords = await GetGroupedRecordsFromDatabaseAsync();//await GetGroupedRecordsAsync(filePath);

                // Prepare a list of ShiftGroups to pass to XML generation
                var allGroups = new List<ShiftGroup>();
                // 🔄 Define the custom order
                var eventTypeOrder = new Dictionary<string, int>
                {
                    { "Delete", 1 },
                    { "Delete_Create", 2 },
                    { "Create", 3 }
                };
                // Process each employee's records (grouped by EmployeeExternalId and EventTypeGroup)
                foreach (var employeeGroup in groupedRecords)
                {
                    // Split the employee's records into groups based on a 14-hour time gap
                    var groupedByTimeGap = SplitByTimeGap(employeeGroup.OrderBy(r => r.ClockTimeAfterChange), TimeSpan.FromHours(14));

                    foreach (var group in groupedByTimeGap)
                    {

                        var cookedGroup = new List<ClockRecord>(); // Replace ClockRecord with your actual record type
                        // Step 1: Add all original records
                        cookedGroup.AddRange(group.Records);
                        var approveRejectRecords = group.Records.Where(r => r.EventType == "ApproveReject").ToList();
                        if (approveRejectRecords.Any())
                        {
                            foreach (var record in approveRejectRecords)
                            {
                                // Determine the clock type
                                var clockType = record.ClockType;
                                var ClockTimeAfterChange = record.ClockTimeAfterChange;
                                var ClockTimeBeforeChange = record.ClockTimeBeforeChange;
                                // Find all matching 'Create' records with same ClockType
                                var matchingCreateRecords = group.Records
                                    .Where(r => r.EventType == "Create")
                                    .ToList();



                                foreach (var match in matchingCreateRecords)
                                {
                                    // Clone and modify
                                    var duplicate = match.Clone(); // Ensure your ClockRecord implements Clone()
                                    duplicate.EventType = "Delete_Create";
                                    cookedGroup.Add(duplicate);

                                    if (match.ClockType == clockType && match.ClockTimeAfterChange == ClockTimeBeforeChange)
                                    {
                                        match.ClockTimeAfterChange = ClockTimeAfterChange;
                                    }


                                    //if (record.ClockType == clockType)
                                    //{
                                    //    duplicate.ClockTimeAfterChange = record.ClockTimeAfterChange;
                                    //}
                                    //duplicate.EventType = "Create";

                                    // Add to cooked group
                                    //cookedGroup.Add(duplicate);
                                }

                                // Step 2: Remove all ApproveReject records before adding to final output
                                cookedGroup = cookedGroup.Where(r => r.EventType != "ApproveReject").ToList();
                                // Add cooked group to final list
                                //allGroups.Add(new ShiftGroup { Records = cookedGroup });
                                // 🔄 Regroup and sort cookedGroup by EventType in the defined order
                                var finalGroupedByEventType = cookedGroup
                                    .GroupBy(r => r.EventType)
                                    .OrderBy(g => eventTypeOrder.ContainsKey(g.Key) ? eventTypeOrder[g.Key] : int.MaxValue) // Unknown types go last
                                    .ToList();

                                foreach (var eventTypeGroup in finalGroupedByEventType)
                                {
                                    allGroups.Add(new ShiftGroup
                                    {
                                        Records = eventTypeGroup.ToList()
                                    });
                                }
                            }
                        }
                        else
                        {
                            // No ApproveReject records, so just add the group as-is
                            allGroups.Add(new ShiftGroup
                            {
                                Records = cookedGroup
                            });
                        }

                    }
                    // Add to the list of all groups
                    //allGroups.AddRange(groupedByTimeGap);
                }

                // Generate XML for all groups
                GenerateManhattanPunchXML(allGroups);

                // Log processing completion details
                DateTime endTime = DateTime.Now;
                LoggerObserver.LogFileProcessed($"Finished processing Manhattan punch CSV: {filePath} at {endTime}");
                TimeSpan duration = endTime - startTime;
                LoggerObserver.LogFileProcessed($"Time taken to process the file: {duration.TotalSeconds:F2} seconds.");
            }
            catch (FileNotFoundException ex)
            {
                LoggerObserver.Error(ex, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                LoggerObserver.Error(ex, "Unauthorized access during file processing.");
            }
            catch (IOException ex)
            {
                LoggerObserver.Error(ex, "I/O error occurred during file processing.");
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "An unexpected error occurred during processing.");
                throw; // Re-throw the exception to ensure proper visibility of critical errors
            }
        }

        private async Task<IEnumerable<IGrouping<object, ClockRecord>>> GetGroupedRecordsFromDatabaseAsync()
        {

            IEnumerable<IGrouping<object, ClockRecord>> groupedRecords = null;

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                var query = @"
                            WITH CurrentRecords AS (
                                    SELECT * FROM legion_time_clock_change_fact WHERE is_current = 1
                                ),
                                OldRecords AS (
                                    SELECT * FROM legion_time_clock_change_fact WHERE is_current = 0
                                ),
                                TimeDifferences AS (
                                    SELECT 
                                        cr.emp_external_id,
                                        cr.after_change_clock_time AS ClockTime1,
                                        orr.after_change_clock_time AS ClockTime2,
                                        ABS(DATEDIFF(SECOND, orr.after_change_clock_time, cr.after_change_clock_time)) / 3600.0 AS HourDifference
                                    FROM CurrentRecords cr
                                    INNER JOIN OldRecords orr
                                        ON cr.emp_external_id = orr.emp_external_id
                                        AND ABS(DATEDIFF(SECOND, orr.after_change_clock_time, cr.after_change_clock_time)) / 3600.0 <= @DaysOffset
                                )
                                SELECT DISTINCT r.*
                                FROM legion_time_clock_change_fact r
                                LEFT JOIN (
                                    SELECT cr.emp_external_id, cr.after_change_clock_time AS ClockTime FROM CurrentRecords cr
                                    UNION ALL
                                    SELECT td.emp_external_id, td.ClockTime2 AS ClockTime FROM TimeDifferences td
                                ) filtered_records
                                    ON r.emp_external_id = filtered_records.emp_external_id
                                    AND r.after_change_clock_time = filtered_records.ClockTime
                                WHERE r.is_current = 1 
                                   OR (r.emp_external_id IN (SELECT emp_external_id FROM CurrentRecords)
                                       AND r.is_current = 0
                                       AND r.after_change_clock_time IN (SELECT ClockTime2 FROM TimeDifferences))
                                ORDER BY r.emp_external_id, r.after_change_clock_time";

                using (var command = new SqlCommand(query, connection))
                {
                    // Add the parameter value for DaysOffset
                    command.Parameters.AddWithValue("@DaysOffset", fetchRecordsOlderThanDays);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        var records = new List<ClockRecord>();

                        while (await reader.ReadAsync())
                        {
                            var clockRecord = new ClockRecord
                            {
                                LocationExternalId = Convert.ToInt32(reader["location_external_id"]),
                                EmployeeExternalId = Convert.ToInt32(reader["emp_external_id"]),
                                ClockType = reader["clock_type"]?.ToString(),
                                ClockTimeBeforeChange = reader["before_change_clock_time"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["before_change_clock_time"],
                                ClockTimeAfterChange = reader["after_change_clock_time"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["after_change_clock_time"],
                                ClockWorkRoleAfterChange = reader["after_change_work_role"]?.ToString(),
                                EventType = reader["event_type"]?.ToString()
                            };

                            if (LocationMapping.TryGetValue(clockRecord.LocationExternalId, out var locationData))
                            {
                                //clockRecord.LocationName = locationData.LocationName;
                                clockRecord.ManhattanWarehouseId = locationData.warehouse_id;
                            }

                            if (TimeZoneMapping.TryGetValue(clockRecord.LocationExternalId.ToString(), out var timeZoneData) && !string.IsNullOrWhiteSpace(timeZoneData.TimeZone))
                            {
                                try
                                {
                                    string windowsTimeZone = TZConvert.IanaToWindows(timeZoneData.TimeZone);
                                    var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZone);
                                    //var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneData.TimeZone);
                                    clockRecord.ClockTimeBeforeChange = ConvertToLocalTime(clockRecord.ClockTimeBeforeChange, timeZoneInfo);
                                    clockRecord.ClockTimeAfterChange = ConvertToLocalTime(clockRecord.ClockTimeAfterChange, timeZoneInfo);
                                }
                                catch (TimeZoneNotFoundException)
                                {
                                    LoggerObserver.LogFileProcessed($"Invalid TimeZone: {timeZoneData.TimeZone} for LocationExternalId: {clockRecord.LocationExternalId}");
                                }
                                catch (InvalidTimeZoneException)
                                {
                                    LoggerObserver.LogFileProcessed($"Invalid TimeZone data: {timeZoneData.TimeZone} for LocationExternalId: {clockRecord.LocationExternalId}");
                                }
                            }

                            records.Add(clockRecord);
                        }

                        groupedRecords = records
                            .GroupBy(r => new
                            {
                                r.EmployeeExternalId,
                                EventTypeGroup = (r.EventType == "Create" || r.EventType == "ApproveReject")
                                    ? "Create_ApproveReject"
                                    : r.EventType
                            });
                    }
                }
            }

            return groupedRecords;
        }


        public void ReadClockRecordsFromFileAndInsertToDatabase(string filePath)
        {
            const int batchSize = 1000;
            var skippedRecords = new List<string>(); // To store skipped records
            // List to hold the result set as ClockRecord objects
            var clockRecords = new List<ClockRecord>();
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                using (var resetCommand = new SqlCommand("UPDATE legion_time_clock_change_fact SET is_current = 0", connection))
                {
                    resetCommand.ExecuteNonQuery();
                }


                using (var getBatchIdCommand = new SqlCommand("SELECT MAX(INSERT_BATCH_ID) FROM legion_time_clock_change_fact", connection))
                {
                    var result = getBatchIdCommand.ExecuteScalar();
                    if (result != DBNull.Value && result != null)
                    {
                        insertBatchId = Convert.ToInt32(result) + 1; // Increment last batch ID
                    }
                }

                SqlTransaction transaction = connection.BeginTransaction();

                try
                {
                    var insertCommand = @"
                INSERT INTO legion_time_clock_change_fact (
                    time_clock_change_id,
                    location_id,
                    location_external_id,
                    last_modified_dt,
                    last_modified_by_emp_id,
                    last_modified_by_external_emp_id,
                    emp_id,
                    emp_external_id,
                    time_clock_id,
                    clock_type,
                    before_change_clock_time,
                    before_change_clock_location_id,
                    before_change_clock_location,
                    before_change_clock_work_role_id,
                    before_change_clock_work_role,
                    before_change_clock_source,
                    before_change_clock_note,
                    after_change_clock_time,
                    after_change_location_id,
                    after_change_clock_location,
                    after_change_work_role_id,
                    after_change_work_role,
                    after_change_clock_source,
                    after_change_clock_note,
                    event_type,
                    is_current,
                    INSERT_BATCH_ID,
                    INSERT_LOAD_DT
                )
                SELECT 
                    @TimeClockChangeId,
                    @LocationId,
                    @LocationExternalId,
                    @LastModifiedDt,
                    @LastModifiedByEmployeeId,
                    @LastModifiedByExternalEmployeeId,
                    @EmployeeId,
                    @EmployeeExternalId,
                    @TimeClockId,
                    @ClockType,
                    @ClockTimeBeforeChange,
                    @ClockLocationIdBeforeChange,
                    @ClockLocationBeforeChange,
                    @ClockWorkRoleIdBeforeChange,
                    @ClockWorkRoleBeforeChange,
                    @ClockSourceBeforeChange,
                    @ClockNoteBeforeChange,
                    @ClockTimeAfterChange,
                    @ClockLocationIdAfterChange,
                    @ClockLocationAfterChange,
                    @ClockWorkRoleIdAfterChange,
                    @ClockWorkRoleAfterChange,
                    @ClockSourceAfterChange,
                    @ClockNoteAfterChange,
                    @EventType,
                    @IsCurrent,
                    @InsertBatchId,
                    @InsertLoadDt
                    WHERE EXISTS (
                    SELECT 1 FROM warehouse_location WHERE location_id = @LocationExternalId
                    ) 
                    AND NOT EXISTS (
                    SELECT 1 FROM legion_time_clock_change_fact
                    WHERE location_external_id = @LocationExternalId
                      AND emp_external_id = @EmployeeExternalId  
                      AND clock_type = @ClockType
                      AND event_type = @EventType
                      AND (
                           (@ClockTimeBeforeChange IS NULL OR @ClockTimeBeforeChange = '') 
                           OR before_change_clock_time = @ClockTimeBeforeChange
                          )
                      AND (
                           (@ClockTimeAfterChange IS NULL OR @ClockTimeAfterChange = '') 
                           OR after_change_clock_time = @ClockTimeAfterChange
                          )
                );";

                    using (var command = new SqlCommand(insertCommand, connection, transaction))
                    {
                        using (var reader = new StreamReader(filePath))
                        {
                            reader.ReadLine(); // Skip header
                            int batchCount = 0;
                            string line;

                            while ((line = reader.ReadLine()) != null)
                            {
                                try
                                {
                                    var parts = line.Split(',');
                            

                                    int locationId;
                                    if (!int.TryParse(parts[2]?.Trim(), out locationId))
                                    {
                                        LoggerObserver.OnFileFailed($"Invalid LocationId - skipped: {parts[2]} | Line: {line}");
                                        skippedRecords.Add(line);
                                        continue;
                                    }

                                    command.Parameters.Clear();
                                    command.Parameters.AddWithValue("@TimeClockChangeId", parts[0]?.Trim());
                                    command.Parameters.AddWithValue("@LocationId", parts[1]?.Trim());
                                    command.Parameters.AddWithValue("@LocationExternalId", parts[2]?.Trim());
                                    //var locationPart = parts[2]?.Trim();

                                    //if (int.TryParse(locationPart, out int directLocationId))
                                    //{
                                    //    // If it's purely numeric, use it as is
                                    //    command.Parameters.AddWithValue("@LocationExternalId", directLocationId);
                                    //}
                                    //else
                                    //{
                                    //    // If it contains _XX, extract digits before _
                                    //    var match = Regex.Match(locationPart, @"^(\d+)_([A-Z]{2})$");
                                    //    int? locationExternalId = match.Success ? int.Parse(match.Groups[1].Value) : (int?)null;

                                    //    command.Parameters.AddWithValue("@LocationExternalId", (object?)locationExternalId ?? DBNull.Value);
                                    //}
                                    command.Parameters.AddWithValue("@LastModifiedDt", DateTime.Parse(parts[3], CultureInfo.InvariantCulture));
                                    command.Parameters.AddWithValue("@LastModifiedByEmployeeId", parts[4]?.Trim());
                                    command.Parameters.AddWithValue("@LastModifiedByExternalEmployeeId", parts[5]?.Trim());
                                    command.Parameters.AddWithValue("@EmployeeId", parts[6]?.Trim());
                                    //command.Parameters.AddWithValue("@EmployeeExternalId", parts[7]?.Trim());
                                    int employeeExternalId;
                                    object employeeExternalIdValue = int.TryParse(parts[7]?.Trim(), out employeeExternalId)
                                        ? (object)employeeExternalId
                                        : DBNull.Value;
                                    if (employeeExternalIdValue == DBNull.Value)
                                    {
                                        LoggerObserver.Info($"Invalid Employee ID..........{parts[7]?.Trim()}");
                                    }
                                    command.Parameters.AddWithValue("@EmployeeExternalId", employeeExternalIdValue);
                                    command.Parameters.AddWithValue("@TimeClockId", parts[8]?.Trim());
                                    command.Parameters.AddWithValue("@ClockType", parts[9]?.Trim());
                                    command.Parameters.AddWithValue("@ClockTimeBeforeChange", string.IsNullOrWhiteSpace(parts[10])
                                        ? DBNull.Value
                                        : (object)DateTime.Parse(parts[10], CultureInfo.InvariantCulture));
                                    command.Parameters.AddWithValue("@ClockLocationIdBeforeChange", parts[11]?.Trim());
                                    command.Parameters.AddWithValue("@ClockLocationBeforeChange", parts[12]?.Trim());
                                    command.Parameters.AddWithValue("@ClockWorkRoleIdBeforeChange", parts[13]?.Trim());
                                    command.Parameters.AddWithValue("@ClockWorkRoleBeforeChange", parts[14]?.Trim());
                                    command.Parameters.AddWithValue("@ClockSourceBeforeChange", parts[15]?.Trim());
                                    command.Parameters.AddWithValue("@ClockNoteBeforeChange", parts[16]?.Trim());
                                    command.Parameters.AddWithValue("@ClockTimeAfterChange", string.IsNullOrWhiteSpace(parts[17])
                                        ? DBNull.Value
                                        : (object)DateTime.Parse(parts[17], CultureInfo.InvariantCulture));
                                    command.Parameters.AddWithValue("@ClockLocationIdAfterChange", parts[18]?.Trim());
                                    command.Parameters.AddWithValue("@ClockLocationAfterChange", parts[19]?.Trim());
                                    command.Parameters.AddWithValue("@ClockWorkRoleIdAfterChange", parts[20]?.Trim());
                                    command.Parameters.AddWithValue("@ClockWorkRoleAfterChange", parts[21]?.Trim());
                                    command.Parameters.AddWithValue("@ClockSourceAfterChange", parts[22]?.Trim());
                                    command.Parameters.AddWithValue("@ClockNoteAfterChange", parts[23]?.Trim());
                                    command.Parameters.AddWithValue("@EventType", parts[24]?.Trim());
                                    command.Parameters.AddWithValue("@IsCurrent", 1);
                                    command.Parameters.AddWithValue("@InsertBatchId", insertBatchId.ToString("D5"));
                                    command.Parameters.AddWithValue("@InsertLoadDt", DateTime.UtcNow);

                                    command.ExecuteNonQuery();
                                    batchCount++;

                                    if (batchCount >= batchSize)
                                    {
                                        transaction.Commit();
                                        transaction.Dispose();
                                        transaction = connection.BeginTransaction();
                                        batchCount = 0;
                                    }
                                }
                                catch (Exception fieldEx)
                                {
                                    LoggerObserver.Error(fieldEx, $"Error while inserting line: {line}");
				                    skippedRecords.Add(line);
                                }
                            }

                            if (batchCount > 0)
                            {
                                transaction.Commit();
                            }
                            // Log skipped records
                            if (skippedRecords.Count > 0)
                            {
                                LoggerObserver.Info($"Skipped {skippedRecords.Count} records due to missing location_id.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerObserver.Error(ex, "Error Occured inside the module ReadClockRecordsFromFileAndInsertToDatabase");
                    transaction.Rollback();
                    throw;
                }
                finally
                {
                    transaction.Dispose();
                }
            }
        }



        // Helper method to convert UTC time to local time based on TimeZoneInfo
        private DateTime? ConvertToLocalTime(DateTime? utcTime, TimeZoneInfo timeZoneInfo)
        {
            LoggerObserver.Info($"UTC Time before converting to timezone is.......................{utcTime}");
            if (utcTime.HasValue)
            {
                LoggerObserver.Info($"After UTC time converted to the timezone specific is ........................{TimeZoneInfo.ConvertTimeFromUtc(utcTime.Value, timeZoneInfo)}");
                return TimeZoneInfo.ConvertTimeFromUtc(utcTime.Value, timeZoneInfo);
            }
            return null;
        }

    }
}
