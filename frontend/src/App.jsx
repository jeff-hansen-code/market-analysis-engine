import { useEffect, useState } from "react";

function App() {
  const [apiStatus, setApiStatus] = useState("loading...");
  const [mlResult, setMlResult] = useState(null);
  const [mlStatus, setMlStatus] = useState("loading...");

  useEffect(() => {
    const baseUrl = import.meta.env.VITE_API_BASE;

    // API health
    fetch(`${baseUrl}/api/health`)
      .then((res) => res.json())
      .then((data) => setApiStatus(data.status))
      .catch(() => setApiStatus("error"));

    // ML dummy
    fetch(`${baseUrl}/api/ml/dummy`)
      .then((res) => res.json())
      .then((data) => {
        setMlResult(data);
        setMlStatus("ok");
      })
      .catch(() => setMlStatus("error"));
  }, []);

  return (
    <div style={{ padding: "1.5rem", fontFamily: "sans-serif" }}>
      <h1>Market Analysis Engine</h1>

      <h2>API</h2>
      <p>Health status: {apiStatus}</p>

      <h2>ML Dummy Prediction</h2>
      <p>Status: {mlStatus}</p>
      {mlResult && <pre>{JSON.stringify(mlResult, null, 2)}</pre>}
    </div>
  );
}

export default App;


/* import { useEffect, useState } from "react";

function App() {
  const [apiStatus, setApiStatus] = useState("loading...");
  const [mlResult, setMlResult] = useState(null);
  const [mlStatus, setMlStatus] = useState("idle");

  useEffect(() => {
    const baseUrl = import.meta.env.VITE_API_BASE;

    // API health
    fetch(`${baseUrl}/api/health`)
      .then((res) => res.json())
      .then((data) => setApiStatus(data.status))
      .catch(() => setApiStatus("error"));

    // ML dummy
    setMlStatus("loading...");
    fetch(`${baseUrl}/api/ml/dummy`)
      .then((res) => res.json())
      .then((data) => {
        setMlResult(data);
        setMlStatus("ok");
      })
      .catch(() => setMlStatus("error"));
  }, []);

  return (
    <div style={{ padding: "1.5rem", fontFamily: "sans-serif" }}>
      <h1>Market Analysis Engine</h1>
      <p>API health status: {apiStatus}</p>

      <h2>ML Dummy Prediction</h2>
      <p>Status: {mlStatus}</p>
      {mlResult && (
        <pre>{JSON.stringify(mlResult, null, 2)}</pre>
      )}
    </div>
  );
}

export default App;
 */


/* import { useEffect, useState } from "react";

function App() {
  const [status, setStatus] = useState("loading...");

  useEffect(() => {
    const baseUrl = import.meta.env.VITE_API_BASE;

    fetch(`${baseUrl}/api/health`)
      .then((res) => res.json())
      .then((data) => setStatus(data.status))
      .catch(() => setStatus("error"));
  }, []);

  return (
    <div style={{ padding: "1.5rem", fontFamily: "sans-serif" }}>
      <h1>Market Analysis Engine</h1>
      <p>API health status: {status}</p>
    </div>
  );
}

export default App;
 */

/* import { useState } from 'react'
import reactLogo from './assets/react.svg'
import viteLogo from '/vite.svg'
import './App.css'

function App() {
  const [count, setCount] = useState(0)

  return (
    <>
      <div>
        <a href="https://vite.dev" target="_blank">
          <img src={viteLogo} className="logo" alt="Vite logo" />
        </a>
        <a href="https://react.dev" target="_blank">
          <img src={reactLogo} className="logo react" alt="React logo" />
        </a>
      </div>
      <h1>Vite + React</h1>
      <div className="card">
        <button onClick={() => setCount((count) => count + 1)}>
          count is {count}
        </button>
        <p>
          Edit <code>src/App.jsx</code> and save to test HMR
        </p>
      </div>
      <p className="read-the-docs">
        Click on the Vite and React logos to learn more
      </p>
    </>
  )
}

export default App */
