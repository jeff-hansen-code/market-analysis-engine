using System;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MarketAnalysis.Functions
{
    public class IngestDummyData
    {
        private readonly ILogger _logger;

        public IngestDummyData(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<IngestDummyData>();
        }

        // Runs every minute: "0 * * * * *"
        [Function("IngestDummyData")]
        public void Run([TimerTrigger("0 * * * * *")] TimerInfo myTimer)
        {
            var now = DateTime.UtcNow;
            _logger.LogInformation(
                "IngestDummyData timer triggered at {Time}. This is a placeholder for future stock data ingestion.",
                now);
        }
    }
}
