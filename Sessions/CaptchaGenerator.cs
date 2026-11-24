namespace AuthBaseXiHan.Sessions;

/// <summary>
/// 图形验证码生成器：输出 SVG（纯字符串拼装，无 System.Drawing 等原生依赖，AOT 安全）
/// </summary>
internal static class CaptchaGenerator
{
    /// <summary>字符集：去掉 0/O、1/I/l 等易混淆字符</summary>
    private const string Alphabet = "23456789abcdefghjkmnpqrstuvwxyz";

    private const int CodeLength = 4;
    private const int Width = 132;
    private const int Height = 44;

    /// <summary>生成随机 id、4 位码与带干扰线的 SVG 图像</summary>
    public static (string Id, string Code, string Svg) New()
    {
        var random = Random.Shared;
        var id = Guid.NewGuid().ToString("N");
        var code = random.GetItems<char>(Alphabet, CodeLength);

        var svg = new System.Text.StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\">");

        // 浅色底
        svg.Append($"<rect width=\"100%\" height=\"100%\" fill=\"#f2f4f7\"/>");

        // 干扰线：随机曲线 3 条
        for (var i = 0; i < 3; i++)
        {
            var (x1, y1) = (random.Next(Width), random.Next(Height));
            var (x2, y2) = (random.Next(Width), random.Next(Height));
            var (cx, cy) = (random.Next(Width), random.Next(Height));
            var color = RandomColor(random, light: false);
            svg.Append(
                $"<path d=\"M {x1} {y1} Q {cx} {cy} {x2} {y2}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1\" opacity=\"0.5\"/>");
        }

        // 干扰点：20 个
        for (var i = 0; i < 20; i++)
        {
            svg.Append(
                $"<circle cx=\"{random.Next(Width)}\" cy=\"{random.Next(Height)}\" r=\"1\" fill=\"{RandomColor(random, light: true)}\"/>");
        }

        // 逐字符：随机旋转（±25°）、随机纵向偏移、随机颜色与字号
        var step = (Width - 24) / CodeLength;
        for (var i = 0; i < CodeLength; i++)
        {
            var x = 16 + i * step + random.Next(-3, 4);
            var y = Height / 2 + 8 + random.Next(-4, 5);
            var angle = random.Next(-25, 26);
            var fontSize = random.Next(22, 28);
            var color = RandomColor(random, light: false);
            svg.Append(
                $"<text x=\"{x}\" y=\"{y}\" transform=\"rotate({angle} {x} {y})\" " +
                $"font-family=\"Georgia, 'Times New Roman', serif\" font-size=\"{fontSize}\" " +
                $"font-weight=\"bold\" fill=\"{color}\" text-anchor=\"middle\">{code[i]}</text>");
        }

        svg.Append("</svg>");
        return (id, new string(code), svg.ToString());
    }

    /// <summary>随机颜色：字符用深色（可读），干扰点用浅色（不喧宾夺主）</summary>
    private static string RandomColor(Random random, bool light) => light
        ? $"rgb({random.Next(160, 220)},{random.Next(160, 220)},{random.Next(160, 220)})"
        : $"rgb({random.Next(30, 130)},{random.Next(30, 130)},{random.Next(30, 130)})";
}
