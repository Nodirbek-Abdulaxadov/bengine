import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { loadPersisted } from './state/history';
import './index.css';

loadPersisted();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
