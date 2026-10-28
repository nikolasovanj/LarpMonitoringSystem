import { Link } from 'react-router-dom';

export function NotFoundPage({ what = 'This page' }: { what?: string }) {
  return (
    <div className="empty">
      <h1>{what} was not found</h1>
      <p className="muted">
        <Link to="/">Back to the overview</Link>
      </p>
    </div>
  );
}
