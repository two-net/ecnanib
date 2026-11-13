using System.Linq.Expressions;
using System.Security.Cryptography;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Platform.Security;

/// <summary>
/// Where a component stores a secret that <see cref="StoredSecrets"/> encrypted: the column of
/// the value, and the column beside it that names the master key (FR-KEY-6). A component says so
/// with <see cref="StoredSecretColumns.AddStoredSecret{TEntity}"/>, and the platform then moves
/// the values of the column to the active master key when that changes (FR-KEY-7). See
/// docs/security.md.
/// </summary>
public abstract class StoredSecretColumn
{
    private protected StoredSecretColumn(Type entity, string value, string keyId)
    {
        Entity = entity;
        Value = value;
        KeyId = keyId;
    }

    /// <summary>The entity whose rows hold the secret.</summary>
    public Type Entity { get; }

    /// <summary>The property of the entity that holds the encrypted value.</summary>
    public string Value { get; }

    /// <summary>The property of the entity that holds the id of the master key that encrypted the value.</summary>
    public string KeyId { get; }

    /// <summary>The column of the value as the database names it: <c>users.two_factor_secret</c>.</summary>
    internal string NameIn(IModel model)
    {
        var entity = model.FindEntityType(Entity)
            ?? throw new InvalidOperationException($"{Entity.Name} is declared to hold a stored secret, and is not an entity of the model.");
        var value = entity.FindProperty(Value)
            ?? throw new InvalidOperationException($"{Entity.Name}.{Value} is declared to hold a stored secret, and is not stored.");

        return $"{entity.GetTableName()}.{value.GetColumnName()}";
    }

    /// <summary>
    /// Encrypts every value of the column that another master key than the active one encrypted
    /// again, with the active one. A value that cannot be read is left as it is.
    /// </summary>
    /// <param name="reading">The context that the values are read with, one after the other.</param>
    /// <param name="writing">Another context, which writes each value while the first one still reads.</param>
    /// <param name="secrets">What encrypts.</param>
    internal abstract MovedSecrets MoveToTheActiveKey(EcnanibDbContext reading, EcnanibDbContext writing, StoredSecrets secrets);

    /// <summary>How many values of the column another master key than the active one encrypted.</summary>
    internal abstract int CountUnderOtherKeys(EcnanibDbContext context, StoredSecrets secrets);
}

/// <summary>What became of the values of one column that the active master key had not encrypted.</summary>
internal sealed class MovedSecrets
{
    /// <summary>How many are now encrypted with the active master key.</summary>
    public int Moved { get; set; }

    /// <summary>How many name a master key that the configuration file does not list, by the id they name.</summary>
    public Dictionary<string, int> NamingAnUnlistedKey { get; } = new(StringComparer.Ordinal);

    /// <summary>How many do not decrypt with the master key they name, by the id of that key.</summary>
    public Dictionary<string, int> NotDecrypting { get; } = new(StringComparer.Ordinal);

    public static void Count(Dictionary<string, int> byKeyId, string keyId) =>
        byKeyId[keyId] = byKeyId.GetValueOrDefault(keyId) + 1;
}

/// <summary>A column of the rows of <typeparamref name="TEntity"/> that holds a stored secret.</summary>
/// <typeparam name="TEntity">The entity whose rows hold the secret.</typeparam>
public sealed class StoredSecretColumn<TEntity> : StoredSecretColumn
    where TEntity : class
{
    private readonly Expression<Func<TEntity, byte[]?>> _value;
    private readonly Expression<Func<TEntity, string?>> _keyId;
    private readonly Func<TEntity, byte[]?> _valueOf;
    private readonly Func<TEntity, string?> _keyIdOf;
    private readonly Func<TEntity, string> _boundTo;

    /// <param name="value">The property that holds the encrypted value: <c>user => user.TwoFactorSecret</c>.</param>
    /// <param name="keyId">The property that holds the id of the master key that encrypted it.</param>
    /// <param name="boundTo">
    /// What the value of a row is bound to, as it is given to <see cref="StoredSecrets.Encrypt"/>
    /// when the value is stored.
    /// </param>
    /// <exception cref="ArgumentException">One of the two is not a property of the entity.</exception>
    public StoredSecretColumn(
        Expression<Func<TEntity, byte[]?>> value,
        Expression<Func<TEntity, string?>> keyId,
        Func<TEntity, string> boundTo)
        : base(typeof(TEntity), PropertyOf(value, nameof(value)), PropertyOf(keyId, nameof(keyId)))
    {
        ArgumentNullException.ThrowIfNull(boundTo);

        _value = value;
        _keyId = keyId;
        _valueOf = value.Compile();
        _keyIdOf = keyId.Compile();
        _boundTo = boundTo;
    }

    internal override MovedSecrets MoveToTheActiveKey(EcnanibDbContext reading, EcnanibDbContext writing, StoredSecrets secrets)
    {
        var result = new MovedSecrets();

        // Read one after the other, and not all into memory: after the active key has changed,
        // these are all the values there are. What is read is the column as it was when the
        // reading began, so a value that is written below is not met a second time.
        foreach (var row in reading.Set<TEntity>().AsNoTracking().Where(UnderAnotherKeyThan(secrets.ActiveKeyId)).AsEnumerable())
        {
            var keyId = _keyIdOf(row)!;
            var stored = new EncryptedSecret(keyId, _valueOf(row) ?? []);

            if (!secrets.Lists(keyId))
            {
                MovedSecrets.Count(result.NamingAnUnlistedKey, keyId);
                continue;
            }

            EncryptedSecret moved;
            try
            {
                moved = secrets.Reencrypt(stored, _boundTo(row));
            }
            catch (CryptographicException)
            {
                // Changed, or copied from another row, or its id now belongs to another key.
                MovedSecrets.Count(result.NotDecrypting, keyId);
                continue;
            }

            // Only while the row still holds what was read. A row that has changed since, because
            // its secret was replaced or removed, or because another instance was here first, is
            // left alone: what it holds now is newer than this.
            result.Moved += writing.Set<TEntity>()
                .Where(entity => entity == row)
                .Where(Holding(stored.Value))
                .ExecuteUpdate(columns => columns
                    .SetProperty(_value, moved.Value)
                    .SetProperty(_keyId, moved.KeyId));
        }

        return result;
    }

    internal override int CountUnderOtherKeys(EcnanibDbContext context, StoredSecrets secrets) =>
        context.Set<TEntity>().Count(UnderAnotherKeyThan(secrets.ActiveKeyId));

    /// <summary>
    /// The rows that hold a value which names another master key. The id is compared as it is
    /// written, so a value that names the active key in other letters is encrypted again too,
    /// and then names it as the configuration file lists it.
    /// </summary>
    private Expression<Func<TEntity, bool>> UnderAnotherKeyThan(string activeKeyId)
    {
        Expression<Func<string?, bool>> another = keyId => keyId != null && keyId != activeKeyId;

        return About(_keyId, another);
    }

    /// <summary>The rows whose value is still the one that was read.</summary>
    private Expression<Func<TEntity, bool>> Holding(byte[] value)
    {
        Expression<Func<byte[]?, bool>> same = stored => stored == value;

        return About(_value, same);
    }

    /// <summary>
    /// A condition on a property as a condition on the entity. What the condition compares with
    /// stays a variable of it, and so reaches the database as a parameter.
    /// </summary>
    private static Expression<Func<TEntity, bool>> About<TProperty>(
        Expression<Func<TEntity, TProperty>> property, Expression<Func<TProperty, bool>> condition) =>
        Expression.Lambda<Func<TEntity, bool>>(
            new Replacement(condition.Parameters[0], property.Body).Visit(condition.Body),
            property.Parameters);

    private static string PropertyOf<TProperty>(Expression<Func<TEntity, TProperty>> property, string parameter)
    {
        ArgumentNullException.ThrowIfNull(property, parameter);

        return property.Body is MemberExpression { Member: var member, Expression: ParameterExpression }
            ? member.Name
            : throw new ArgumentException($"Name a property of {typeof(TEntity).Name}, as in: row => row.Secret.", parameter);
    }

    /// <summary>Puts an expression in the place of a parameter.</summary>
    private sealed class Replacement(ParameterExpression parameter, Expression with) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? with : node;
    }
}

/// <summary>Declares where a component stores its encrypted secrets. See docs/security.md.</summary>
public static class StoredSecretColumns
{
    /// <summary>
    /// Says that a column holds a secret which <see cref="StoredSecrets"/> encrypted. A component
    /// calls this in its <c>Add&lt;Name&gt;()</c>, once for each such column. The platform then
    /// encrypts the values of the column again when the active master key has changed (FR-KEY-7).
    /// </summary>
    /// <param name="services">The services of the application.</param>
    /// <param name="value">The property that holds the encrypted value: <c>user => user.TwoFactorSecret</c>.</param>
    /// <param name="keyId">The property that holds the id of the master key that encrypted it.</param>
    /// <param name="boundTo">
    /// What the value of a row is bound to, as it is given to <see cref="StoredSecrets.Encrypt"/>
    /// when the value is stored.
    /// </param>
    public static IServiceCollection AddStoredSecret<TEntity>(
        this IServiceCollection services,
        Expression<Func<TEntity, byte[]?>> value,
        Expression<Func<TEntity, string?>> keyId,
        Func<TEntity, string> boundTo)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<StoredSecretColumn>(new StoredSecretColumn<TEntity>(value, keyId, boundTo));

        return services;
    }
}
