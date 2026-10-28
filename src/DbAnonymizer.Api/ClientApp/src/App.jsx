import { useEffect } from 'react';
import CreateJob from './pages/CreateJob';
import Jobs from './pages/Jobs';
import PlanReview from './pages/PlanReview';
import Report from './pages/Report';

const pages = {
  '/': ['Обезличивание баз данных', CreateJob],
  '/index.html': ['Обезличивание баз данных', CreateJob],
  '/jobs.html': ['Задачи обезличивания', Jobs],
  '/plan-review.html': ['Ревью плана', PlanReview],
  '/report.html': ['Отчёт о задаче', Report],
};

export default function App() {
  const [title, Page] = pages[window.location.pathname] ?? pages['/'];
  const id = new URLSearchParams(window.location.search).get('id');
  useEffect(() => { document.title = `DbAnonymizer — ${title}`; }, [title]);
  return <Page id={id} />;
}
