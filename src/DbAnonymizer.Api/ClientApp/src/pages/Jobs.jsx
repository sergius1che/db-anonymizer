import { dateLabel } from '../api';
import { Message, useResource } from '../components';

const statuses = ['Создана', 'Загрузка данных', 'Ожидает ревью', 'Заморожен',
  'Обогащение', 'Применение', 'Проверка', 'Завершена', 'Ошибка'];

export default function Jobs() {
  const { data: jobs, error, loading } = useResource('/jobs');
  return <main className="page">
    <h1>Задачи обезличивания</h1>
    <p><a href="/">← На главную</a></p>
    <Message>{error}</Message>
    {loading && <p role="status">Загрузка задач…</p>}
    {jobs?.length === 0 && <p className="empty">Задач пока нет. <a href="/">Создайте новую задачу.</a></p>}
    {!!jobs?.length && <div className="table-scroll"><table>
      <thead><tr><th>ID</th><th>Статус</th><th>Источник</th><th>Создана</th><th>Отчёт</th></tr></thead>
      <tbody>{jobs.map(job => <tr key={job.id}>
        <td><a href={`/plan-review.html?id=${encodeURIComponent(job.id)}`}>{job.id}</a></td>
        <td>{statuses[job.status] ?? `Неизвестно (${job.status})`}</td>
        <td>{job.source?.dumpFilePath || 'live'}</td>
        <td>{dateLabel(job.createdAt)}</td>
        <td>{job.status === 7 ? <a href={`/report.html?id=${encodeURIComponent(job.id)}`}>Отчёт</a> : '—'}</td>
      </tr>)}</tbody>
    </table></div>}
  </main>;
}
