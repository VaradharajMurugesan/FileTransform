using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform_Manhattan.FileProcessing
{
    public static class CsvFileProcessorFactory
    {
        public static async Task<ICsvFileProcessorStrategy> GetProcessorAsync(string processortype, JObject clientSettings)
        {
            if (processortype.Contains("manhattanpunch", StringComparison.OrdinalIgnoreCase))
            {
                return await ManhattanPunchProcessor.CreateAsync(clientSettings);
            }
            else
            {
                throw new ArgumentException("Unknown Processor type.");
            }
        }
    }

}
