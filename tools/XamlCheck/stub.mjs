// Writes what the XAML compiler would generate for MainPage.xaml: a field for every element with an x:Name, and InitializeComponent.
import fs from 'node:fs';
const [, , xamlPath, outPath] = process.argv;
const xaml = fs.readFileSync(xamlPath, 'utf8');
const shapes = new Set(['Rectangle', 'Ellipse', 'Line', 'Path', 'Polygon', 'Polyline']);
const fields = [];
for (const m of xaml.matchAll(/<([A-Za-z][A-Za-z0-9.]*)\b[^>]*?\bx:Name="([A-Za-z0-9_]+)"/g)) {
  const [, type, name] = m;
  fields.push(`    internal ${shapes.has(type) ? 'Windows.UI.Xaml.Shapes.' : ''}${type} ${name} = null!;`);
}
const code = `// Generated from MainPage.xaml by tools/XamlCheck/stub.mjs. Not part of the app.
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
namespace Longhand;
public sealed partial class MainPage
{
${fields.join('\n')}
    void InitializeComponent() { }
}
`;
fs.mkdirSync(outPath.replace(/[\\/][^\\/]*$/, ''), { recursive: true });
fs.writeFileSync(outPath, code);
