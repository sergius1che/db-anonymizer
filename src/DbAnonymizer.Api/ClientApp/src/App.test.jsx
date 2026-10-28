import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import App from './App';

const column = (tableName, jsonPath = '') => ({ schemaName: 'public', tableName,
  columnName: 'data', dataType: 'jsonb', semanticType: 20, strategy: 0,
  isUnique: false, isJsonPath: !!jsonPath, jsonPath, distinctCount: 10, rowCount: 20 });
const draft = { columns: [column('first', '$.name'), column('first', '$.phone'), column('second')], frozenAt: '0001-01-01T00:00:00' };
const json = (data, status = 200) => new Response(JSON.stringify(data), { status });
function open(path, handler) {
  window.history.replaceState({}, '', path);
  vi.stubGlobal('fetch', vi.fn(handler));
  render(<App />);
  return userEvent.setup();
}

describe('React screens', () => {
  it('keeps edits for separate JSON paths across table filters and saves before freezing', async () => {
    let saved;
    let frozen = false;
    const user = open('/plan-review.html?id=job', async (url, options) => {
      if (options?.method === 'PUT') { saved = JSON.parse(options.body); return json(draft); }
      if (options?.method === 'POST') { expect(saved).toBeDefined(); frozen = true; return json({ ...draft, columns: saved, frozenAt: '2026-10-28T12:00:00Z' }); }
      return json(draft);
    });
    await screen.findAllByRole('combobox');
    await user.click(screen.getByRole('button', { name: 'first', exact: true }));
    await user.selectOptions(screen.getAllByRole('combobox')[0], '1');
    await user.selectOptions(screen.getAllByRole('combobox')[1], '3');
    await user.click(screen.getByRole('button', { name: 'second', exact: true }));
    await user.selectOptions(screen.getByRole('combobox'), '6');
    await user.click(screen.getByRole('button', { name: 'Все таблицы' }));
    expect(screen.getAllByRole('combobox').map(s => s.value)).toEqual(['1', '3', '6']);
    await user.click(screen.getByRole('button', { name: 'Заморозить' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Заморозить' })).toBeDisabled());
    expect(frozen).toBe(true);
    expect(saved.map(c => c.strategy)).toEqual([1, 3, 6]);
    expect(saved[0].distinctCount).toBe(10);
  });

  it('reloads a concurrently frozen plan after 409 and locks editing', async () => {
    let reads = 0;
    const user = open('/plan-review.html?id=job', async (_, options) => options?.method
      ? json({}, 409) : json(++reads === 1 ? draft : { ...draft, frozenAt: '2026-10-28T12:00:00Z' }));
    await screen.findAllByRole('combobox');
    await user.click(screen.getByRole('button', { name: 'Заморозить' }));
    await screen.findByText(/План уже заморожен/);
    expect(screen.getAllByRole('combobox').every(s => s.disabled)).toBe(true);
  });

  it('does not permit freeze without a plan id', () => {
    open('/plan-review.html', () => { throw new Error('Unexpected request'); });
    expect(screen.getByRole('button', { name: 'Заморозить' })).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('Не указан id');
  });

  it('submits a live source with seed and zero suffix width', async () => {
    let body;
    const user = open('/', async (_, options) => { body = JSON.parse(options.body); return json({}, 400); });
    await user.click(screen.getByLabelText('Живая БД'));
    await user.type(screen.getByLabelText('Connection string'), 'Host=test');
    await user.type(screen.getByLabelText('Seed (опционально)'), 'seed');
    await user.type(screen.getByLabelText('Ширина суффикса (опционально)'), '0');
    await user.click(screen.getByRole('button', { name: 'Создать задачу' }));
    await screen.findByRole('alert');
    expect(body).toEqual({ source: { connectionString: 'Host=test' }, seed: 'seed', suffixWidth: 0 });
    expect(screen.getByRole('button', { name: 'Создать задачу' })).toBeEnabled();
  });

  it('uploads a dump using multipart form data', async () => {
    let payload;
    const user = open('/', async (url, options) => { payload = { url, body: options.body }; return json({}, 400); });
    const file = new File(['dump'], 'input.dump');
    await user.upload(screen.getByLabelText(/Файл дампа \(custom/), file);
    await user.click(screen.getByRole('button', { name: 'Создать задачу' }));
    await screen.findByRole('alert');
    expect(payload.url).toBe('/jobs/upload');
    expect(payload.body.get('file')).toBe(file);
  });

  it('shows a useful message when a report is not available yet', async () => {
    open('/report.html?id=job', async () => json({}, 404));
    expect(await screen.findByRole('alert')).toHaveTextContent('Отчёт не найден');
  });

  it('renders job data as text and links completed jobs to their report', async () => {
    open('/jobs.html', async () => json([{ id: 'a&b', status: 7, source: { dumpFilePath: '<img src=x>' }, createdAt: 'invalid' }]));
    expect(await screen.findByText('<img src=x>')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Отчёт' })).toHaveAttribute('href', '/report.html?id=a%26b');
    expect(document.querySelector('img')).toBeNull();
  });
});
