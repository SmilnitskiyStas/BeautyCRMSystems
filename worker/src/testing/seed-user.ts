/**
 * Єдиний хелпер сідування користувача з ідентичністю для інтеграційних тестів воркера (ADR-024: users.identity_id NOT NULL,
 * тригер узгодженості пошти). Виконується від імені власника БД (superuser): ідентичність вставляється напряму, членство бере її за поштою.
 * Та сама пошта в кількох tenant = одна ідентичність.
 */
export interface QueryRunner {
  query(sql: string, params?: unknown[]): Promise<unknown>;
}

export async function seedUser(
  db: QueryRunner,
  o: { id: string; tenantId: string; email: string; fullName?: string; passwordHash?: string; role?: string },
): Promise<void> {
  const email = o.email.trim().toLowerCase();
  const hash = o.passwordHash ?? "x";
  await db.query("INSERT INTO identities (email, password_hash, state) VALUES ($1, $2, 'active') ON CONFLICT (email) DO NOTHING", [email, hash]);
  await db.query(
    "INSERT INTO users (id, tenant_id, email, full_name, password_hash, role, identity_id) " +
      "VALUES ($1, $2, $3, $4, $5, $6, (SELECT id FROM identities WHERE email = $7))",
    [o.id, o.tenantId, o.email, o.fullName ?? "U", hash, o.role ?? "owner", email],
  );
}
