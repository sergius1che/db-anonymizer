import { useEffect, useState } from 'react';
import { request } from './api';

export function Message({ children, kind = 'error' }) {
  return children ? <div className={`banner ${kind}`} role={kind === 'error' ? 'alert' : 'status'}>{children}</div> : null;
}

export function useResource(url, missingMessage) {
  const [state, setState] = useState({ data: null, error: '', loading: !!url });
  useEffect(() => {
    if (!url) return;
    const controller = new AbortController();
    setState({ data: null, error: '', loading: true });
    request(url, { signal: controller.signal }).then(data => {
      if (!controller.signal.aborted) setState({ data, error: '', loading: false });
    }).catch(error => {
      if (!controller.signal.aborted) setState({ data: null, loading: false,
        error: error.status === 404 ? missingMessage : `Не удалось загрузить данные: ${error.message}` });
    });
    return () => controller.abort();
  }, [url, missingMessage]);
  return state;
}
