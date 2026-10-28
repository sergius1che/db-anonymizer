import { dateLabel } from '../api';
import { Message, useResource } from '../components';

export default function Report({ id }) {
  const { data: report, error, loading } = useResource(id ? `/reports/${encodeURIComponent(id)}` : null,
    'Отчёт не найден. Отчёт появляется после завершения проверки целостности задачи.');
  return <main className="page">
    <h1>Отчёт обезличивания</h1>
    <p><a href="/jobs.html">К задачам</a></p>
    <Message>{!id ? 'Не указан id отчёта: откройте страницу из списка задач.' : error}</Message>
    {loading && <p role="status">Загрузка отчёта…</p>}
    {report && <>
      <Message kind={report.allChecksPassed ? 'ok' : 'error'}>{report.allChecksPassed
        ? 'Все проверки пройдены — данные готовы к передаче.' : 'Обнаружены проблемы — данные требуют внимания.'}</Message>
      <dl className="meta">{[['Задача', report.jobId], ['Seed', report.seed],
        ['Завершена', dateLabel(report.completedAt)], ['Дамп на выходе', report.outputDumpPath || '—']]
        .map(([label, value]) => <div key={label}><dt>{label}:</dt><dd>{value ?? '—'}</dd></div>)}</dl>
      <div className="table-scroll"><table>
        <thead><tr><th>Проверка</th><th>Результат</th><th>Детали</th></tr></thead>
        <tbody>{report.checks?.length ? report.checks.map((check, index) => <tr key={index}>
          <td>{check.name}</td><td className={check.passed ? 'ok-mark' : 'fail-mark'}>{check.passed ? 'пройдена' : 'не пройдена'}</td>
          <td>{check.detail || '—'}</td>
        </tr>) : <tr><td colSpan="3">Проверок нет.</td></tr>}</tbody>
      </table></div>
    </>}
  </main>;
}
