                    ┌─────────────────────────┐
                    │      Frontend (UI)      │
                    │      React Dashboard    │
                    └─────────────┬───────────┘
                                  │  REST API calls
                                  ▼
                   ┌──────────────────────────────┐
                   │        .NET Web API           │
                   │  (Azure App Service or FN)    │
                   │  - Stock endpoints            │
                   │  - Ratings / insights         │
                   │  - Exposes ML predictions     │
                   └─────────────┬────────────────┘
                                 │
                                 │ calls ML service
                                 ▼
                   ┌──────────────────────────────┐
                   │     Python ML Service        │
                   │ (Model inference endpoint)   │
                   │ - ONNX / pkl model loading   │
                   │ - Prediction logic           │
                   └─────────────┬────────────────┘
                                 │
                                 │
                                 ▼
                ┌──────────────────────────────────────────┐
                │         Azure Functions (Pipelines)       │
                │  - Timer triggers (daily/weekly)          │
                │  - Data ingestion (prices, fundamentals)  │
                │  - Insider / institutional fetch          │
                │  - Cleansing + transformation             │
                │  - Store to DB/storage                    │
                └─────────────────┬─────────────────────────┘
                                  │
                                  ▼
                ┌──────────────────────────────────────────┐
                │                Storage Layer              │
                │   Azure SQL: processed analytics data     │
                │   Blob Storage: raw ingested files        │
                │   Cosmos DB (optional): fast document     │
                └──────────────────────────────────────────┘

