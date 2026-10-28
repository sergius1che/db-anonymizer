export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

export async function request(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) throw new ApiError(response.status, `HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}

export function dateLabel(value) {
  const date = value ? new Date(value) : null;
  return date && !Number.isNaN(date.getTime()) ? date.toLocaleString('ru-RU') : '—';
}
