export function Loading({ label = 'Loading…' }: { label?: string }) {
  return <p className="muted empty">{label}</p>;
}

export function ErrorMessage({ error }: { error: unknown }) {
  const message = error instanceof Error ? error.message : 'Something went wrong';
  return (
    <p className="error" role="alert">
      {message}
    </p>
  );
}
