import { useState } from 'react';
import { request } from '../api';
import { Message } from '../components';

export default function CreateJob() {
  const [source, setSource] = useState('dump');
  const [file, setFile] = useState(null);
  const [connection, setConnection] = useState('');
  const [seed, setSeed] = useState('');
  const [suffix, setSuffix] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  async function submit(event) {
    event.preventDefault();
    setError('');
    if (source === 'live' ? !connection.trim() : !file) {
      setError(source === 'live' ? 'Укажите connection string.' : 'Выберите файл дампа.');
      return;
    }
    setBusy(true);
    try {
      if (source === 'live') {
        const body = { source: { connectionString: connection.trim() } };
        if (seed.trim()) body.seed = seed.trim();
        if (suffix !== '') body.suffixWidth = Number(suffix);
        await request('/jobs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      } else {
        const body = new FormData();
        body.append('file', file);
        if (seed.trim()) body.append('seed', seed.trim());
        if (suffix !== '') body.append('suffixWidth', suffix);
        await request('/jobs/upload', { method: 'POST', body });
      }
      window.location.assign('/jobs.html');
    } catch (err) {
      setError(`Не удалось создать задачу: ${err.message}`);
      setBusy(false);
    }
  }

  return <main className="page narrow">
    <h1>DbAnonymizer</h1>
    <p>Санитизация копий баз данных: персональные и чувствительные данные заменяются
      синтетическими значениями, чтобы дамп можно было безопасно передать командам разработки.</p>
    <h2>Новая задача</h2>
    <form onSubmit={submit}>
      <fieldset disabled={busy}>
        <legend>Источник</legend>
        <div className="radio-row">
          <label><input type="radio" name="source" checked={source === 'dump'} onChange={() => setSource('dump')} /> Файл дампа</label>
          <label><input type="radio" name="source" checked={source === 'live'} onChange={() => setSource('live')} /> Живая БД</label>
        </div>
        <div className="field" hidden={source !== 'dump'}>
          <label htmlFor="dump-file">Файл дампа (custom-формат — pg_dump -Fc)</label>
          <input id="dump-file" type="file" onChange={e => setFile(e.target.files[0] ?? null)} />
          <p className="hint">Только custom-формат (один файл). Для directory-формата (-Fd) — путь на сервере через API (см. README).</p>
        </div>
        <div className="field" hidden={source !== 'live'}>
          <label htmlFor="connection">Connection string</label>
          <input id="connection" type="text" value={connection} onChange={e => setConnection(e.target.value)} placeholder="Host=...;Port=5432;Database=...;Username=...;Password=..." />
          <p className="hint">Роль источника должна иметь право только на чтение (см. «Ограничения» в README).</p>
        </div>
        <div className="field">
          <label htmlFor="seed">Seed (опционально)</label>
          <input id="seed" type="text" value={seed} onChange={e => setSeed(e.target.value)} placeholder="автоматически, если не указан" />
        </div>
        <div className="field">
          <label htmlFor="suffix">Ширина суффикса (опционально)</label>
          <input id="suffix" type="number" min="0" step="1" value={suffix} onChange={e => setSuffix(e.target.value)} placeholder="6 по умолчанию" />
        </div>
        <button type="submit" className="primary">{busy ? 'Создание…' : 'Создать задачу'}</button>
      </fieldset>
    </form>
    <Message>{error}</Message>
    <h2>Экраны</h2>
    <a href="/jobs.html">Задачи обезличивания</a> — список задач, ревью плана и отчёты о результате.
  </main>;
}
