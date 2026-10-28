import { useEffect, useState } from 'react';
import { request, dateLabel } from '../api';
import { Message } from '../components';

const strategies = [[0, 'Keep'], [1, 'Fake'], [3, 'Format'], [5, 'Shift'], [6, 'Redact'], [4, 'Secret'], [2, 'Derive']];
const semantics = ['Не определён', 'Имя', 'Фамилия', 'Отчество', 'ФИО', 'E-mail', 'Логин',
  'Телефон', 'Номер карты', 'ИНН', 'СНИЛС', 'Номер паспорта', 'Адрес', 'Организация',
  'Дата рождения', 'Хэш пароля', 'Соль', 'Токен', 'Внешний ID', 'Свободный текст', 'JSON'];
const isFrozen = plan => !!plan?.frozenAt && !plan.frozenAt.startsWith('0001-01-01');
const tableKey = column => JSON.stringify([column.schemaName, column.tableName]);

export default function PlanReview({ id }) {
  const [plan, setPlan] = useState(null);
  const [filter, setFilter] = useState(null);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(!!id);
  const [message, setMessage] = useState(null);
  const url = id ? `/plans/${encodeURIComponent(id)}` : null;

  useEffect(() => {
    if (!url) return;
    const controller = new AbortController();
    request(url, { signal: controller.signal }).then(data => {
      if (!controller.signal.aborted) setPlan(data);
    }).catch(err => {
      if (!controller.signal.aborted) setMessage({ kind: 'error', text: err.status === 404
        ? 'План не найден. План создаётся после интроспекции задачи.' : `Не удалось загрузить план: ${err.message}` });
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [url]);

  const frozen = isFrozen(plan);
  const columns = plan?.columns ?? [];
  const groups = new Map();
  for (const column of columns) {
    if (!groups.has(column.schemaName)) groups.set(column.schemaName, new Set());
    groups.get(column.schemaName).add(column.tableName);
  }

  function updateStrategy(index, strategy) {
    setPlan(previous => ({ ...previous, columns: previous.columns.map((column, i) => i === index ? { ...column, strategy } : column) }));
  }

  async function freeze() {
    if (!plan || frozen || busy || !columns.length) return;
    setBusy(true);
    setMessage(null);
    try {
      await request(url, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(columns) });
      const result = await request(`${url}/freeze`, { method: 'POST' });
      setPlan(result);
      setMessage({ kind: 'ok', text: 'План заморожен — запущено обогащение.' });
    } catch (err) {
      if (err.status === 409) {
        try {
          setPlan(await request(url));
          setMessage({ kind: 'error', text: 'Состояние плана изменилось. Загружена актуальная версия.' });
        } catch (reloadError) {
          setPlan(null);
          setMessage({ kind: 'error', text: `Не удалось обновить план: ${reloadError.message}. Перезагрузите страницу.` });
        }
      } else setMessage({ kind: 'error', text: `Ошибка: ${err.message}` });
    } finally { setBusy(false); }
  }

  return <>
    <header>
      <h1>Ревью плана обезличивания</h1>
      <span className="counter">{columns.filter(c => c.strategy !== 0).length} из {columns.length} помечено</span>
      <button className="primary" disabled={loading || busy || frozen || !columns.length} onClick={freeze}>Заморозить</button>
      <a href="/jobs.html">К задачам</a>
    </header>
    <div className="messages">
      <Message>{!id ? 'Не указан id плана: откройте страницу из списка задач.' : null}</Message>
      {message && <Message kind={message.kind}>{message.text}</Message>}
      {frozen && <Message kind="ok">План уже заморожен ({dateLabel(plan.frozenAt)}). Изменения недоступны.</Message>}
      {loading && <p role="status">Загрузка плана…</p>}
    </div>
    {plan && <div className="layout">
      <nav className="panel tree" aria-label="Схемы и таблицы">
        <div className="schema">Схемы и таблицы</div>
        <ul>{[...groups.keys()].sort().map(schema => <li key={schema}>
          <div className="schema">{schema}</div>
          <ul>{[...groups.get(schema)].sort().map(table => {
            const key = JSON.stringify([schema, table]);
            return <li key={table}><button className={`table ${filter === key ? 'active' : ''}`} onClick={() => setFilter(key)}>{table}</button></li>;
          })}</ul>
        </li>)}</ul>
        <button className={`table ${filter === null ? 'active' : ''}`} onClick={() => setFilter(null)}>Все таблицы</button>
      </nav>
      <section className="panel columns">
        <p className="hint">{frozen ? 'План заморожен — изменения недоступны.' : 'Отметьте стратегии для чувствительных колонок.'}</p>
        <div className="table-scroll"><table>
          <thead><tr><th>Колонка</th><th>Тип</th><th>Семантический тип</th><th>Стратегия</th></tr></thead>
          <tbody>{columns.map((column, index) => filter !== null && filter !== tableKey(column) ? null : <tr key={index}>
            <td><strong>{column.columnName}</strong>{column.isJsonPath && column.jsonPath && <><br /><span className="hint">JSON: {column.jsonPath}</span></>}</td>
            <td>{column.dataType || '—'}</td>
            <td>{semantics[column.semanticType] ?? `Неизвестно (${column.semanticType})`}</td>
            <td><select aria-label={`Стратегия ${column.schemaName}.${column.tableName}.${column.columnName}${column.isJsonPath ? ` ${column.jsonPath}` : ''}`}
              disabled={frozen || busy} value={column.strategy} onChange={e => updateStrategy(index, Number(e.target.value))}>
              {strategies.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
            </select></td>
          </tr>)}</tbody>
        </table></div>
        {!columns.length && <p className="empty">В плане нет колонок.</p>}
      </section>
    </div>}
  </>;
}
