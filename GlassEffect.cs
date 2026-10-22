using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows;

namespace Sharm;

public sealed class GlassEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    { UriSource = new Uri("pack://application:,,,/Sharm;component/Assets/Glass.ps", UriKind.Absolute) };
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(GlassEffect), 0);
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public GlassEffect() { PixelShader = Shader; UpdateShaderValue(InputProperty); }
}
