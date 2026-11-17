export function distinctBy<T>(items: readonly T[] | undefined, key: (item: T) => string): T[] {
  const seen = new Set<string>();
  const result: T[] = [];
  for (const item of items ?? []) {
    const id = key(item).trim().toLocaleLowerCase();
    if (!id || seen.has(id)) continue;
    result.push(item);
    seen.add(id);
  }
  return result;
}
