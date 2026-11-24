using Microsoft.Extensions.Options;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Operations;

// 通用分页基础设施：所有列表接口共用的分页查询契约、参数校验与响应封装。
// 三要素：Page（页码）/ PageSize（每页条数）/ Query（关键字筛选，各列表自己决定匹配哪个字段）。
// PageSize 全局可配置：appsettings "Paging" 节（DefaultPageSize / MaxPageSize / MaxPage），
// 改配置即可调整所有列表的默认页大小与上限，无需改代码。

/// <summary>分页配置（绑定 appsettings "Paging" 节）</summary>
/// <remarks>
/// Program.cs 里 Configure&lt;PagingOptions&gt;(GetSection("Paging")) 绑定；
/// 列表操作注入 IOptions&lt;PagingOptions&gt; 读取。字段缺省值即代码内默认，配置可不写该节。
/// </remarks>
public sealed class PagingOptions
{
    /// <summary>配置节名</summary>
    public const string SectionName = "Paging";

    /// <summary>默认每页条数（请求不传 pageSize 时使用）</summary>
    public int DefaultPageSize { get; set; } = 10;

    /// <summary>每页条数上限（防一次拉全表）</summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>页码上限（防恶意深翻页打爆数据库）</summary>
    public int MaxPage { get; set; } = 10000;
}

/// <summary>通用分页查询请求（各列表接口共用；Query 的匹配字段由列表自行决定）</summary>
/// <param name="Page">页码，从 1 开始，默认 1</param>
/// <param name="PageSize">每页条数，不传用服务端配置的默认值（Paging:DefaultPageSize），上限 Paging:MaxPageSize</param>
/// <param name="Query">关键字筛选（可选）：对列表主要文本字段做包含匹配（忽略大小写）</param>
public sealed record SessionPagedRequest(int Page = 1, int? PageSize = null, string? Query = null);

/// <summary>通用分页响应</summary>
/// <param name="Items">当前页数据列表</param>
/// <param name="Total">筛选后的总条数（供前端计算页码）</param>
/// <param name="Page">当前页码（回显）</param>
/// <param name="PageSize">生效的每页条数（回显，含服务端默认值解析结果）</param>
public sealed record SessionPagedResponse<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);

/// <summary>分页参数校验与规范化助手</summary>
public static class Paging
{
    /// <summary>错误码：分页参数超出范围（各列表复用同一码，文案按语言翻译）</summary>
    public const string ErrorCode = "PAGING_VALIDATION_001";

    /// <summary>本地化 messageKey</summary>
    public const string ErrorMessageKey = "paging.out_of_range";

    /// <summary>
    /// 校验并规范化分页参数：页码 1-MaxPage，页大小 1-MaxPageSize（不传用配置默认值）。
    /// 校验失败返回 <see cref="Result"/> 失败（400），成功返回规范化的 (Page, PageSize)。
    /// </summary>
    public static Result<(int Page, int PageSize)> Validate(
        SessionPagedRequest request, PagingOptions options)
    {
        if (request.Page is < 1 || request.Page > options.MaxPage)
        {
            return Result.Failure<(int, int)>(
                Error.Validation(ErrorCode, ErrorMessageKey)
                    .WithText($"页码须为 1-{options.MaxPage}"));
        }

        var pageSize = request.PageSize ?? options.DefaultPageSize;
        if (pageSize is < 1 || pageSize > options.MaxPageSize)
        {
            return Result.Failure<(int, int)>(
                Error.Validation(ErrorCode, ErrorMessageKey)
                    .WithText($"每页条数须为 1-{options.MaxPageSize}（不传默认 {options.DefaultPageSize}）"));
        }

        return Result.Success((request.Page, pageSize));
    }

    /// <summary>关键字筛选的 LIKE 通配符转义：用户输入里的 % _ [ 按字面匹配，不当通配符</summary>
    public static string EscapeLike(string query) =>
        query.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
