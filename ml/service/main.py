from fastapi import FastAPI
from pydantic import BaseModel

app = FastAPI()


class PredictRequest(BaseModel):
    ticker: str | None = None


@app.get("/health")
def health():
    return {"status": "ml-ok"}


@app.post("/predict")
def predict(req: PredictRequest):
    return {
        "ticker": req.ticker,
        "expected_return_5y": 0.12,  # dummy fixed value
        "confidence": 0.55,  # dummy fixed value
    }
