using System.Windows;
using System.Windows.Media;

namespace HiddenReader.Core.Reading;

/// <summary>
/// 正文字号、行距与内边距。所有尺寸均为 DIP。
/// </summary>
public sealed class TextSettings
{
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 18;
    public double LineHeightFactor { get; set; } = 1.75;
    public double Padding { get; set; } = 14;

    public double LineHeight => Math.Round(FontSize * LineHeightFactor, 2);

    public Typeface CreateTypeface() => new(new FontFamily(FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public TextSettings Clone() => (TextSettings)MemberwiseClone();
}
