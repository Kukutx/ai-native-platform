using System.Linq.Expressions;
using System.Reflection;
using AiNative.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Infrastructure.Persistence;

/// <summary>
/// 用 ILIKE 实现的包含匹配。模式经过转义后作为参数传入，配合 gin_trgm_ops 索引，包含匹配也能走索引。
/// </summary>
internal sealed class PostgresTextMatcher : ITextMatcher
{
    // 必须显式指定转义符：不指定时 Npgsql 会生成 ESCAPE ''，反斜杠转义失效。
    private static readonly MethodInfo ILike = typeof(NpgsqlDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    private static readonly ConstantExpression EscapeCharacter = Expression.Constant(@"\");

    public IQueryable<T> WhereContains<T>(IQueryable<T> source, string term, params Expression<Func<T, string>>[] fields)
    {
        if (string.IsNullOrWhiteSpace(term) || fields.Length == 0)
        {
            return source;
        }

        // 通过闭包对象引用模式，EF 会把它当作 SQL 参数而不是拼进语句。
        var pattern = Expression.Field(Expression.Constant(new Pattern(ContainsPattern(term))), nameof(Pattern.Value));
        var row = Expression.Parameter(typeof(T), "x");
        Expression? body = null;
        foreach (var field in fields)
        {
            var value = new ParameterReplacer(field.Parameters[0], row).Visit(field.Body);
            var match = Expression.Call(ILike, Expression.Constant(EF.Functions), value, pattern, EscapeCharacter);
            body = body is null ? match : Expression.OrElse(body, match);
        }

        return source.Where(Expression.Lambda<Func<T, bool>>(body!, row));
    }

    /// <summary>
    /// 用反斜杠转义 LIKE 通配符后两端加 %。
    /// </summary>
    internal static string ContainsPattern(string term)
        => "%" + term.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private sealed class Pattern(string value)
    {
        public readonly string Value = value;
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
