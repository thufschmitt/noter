// Writes what the XAML compiler would generate for a page (MainPage.xaml, ScorePage.xaml): a field for every element with an
// x:Name, and InitializeComponent, in the partial class its x:Class names.
import fs from 'node:fs';
const [, , xamlPath, outPath] = process.argv;
const xaml = fs.readFileSync(xamlPath, 'utf8');
const cls = /x:Class="([A-Za-z0-9_.]+)"/.exec(xaml)?.[1] ?? 'Longhand.MainPage';
const ns = cls.slice(0, cls.lastIndexOf('.')), name = cls.slice(cls.lastIndexOf('.') + 1);
const shapes = new Set(['Rectangle', 'Ellipse', 'Line', 'Path', 'Polygon', 'Polyline']);
const fields = [];
for (const m of xaml.matchAll(/<([A-Za-z][A-Za-z0-9.]*)\b[^>]*?\bx:Name="([A-Za-z0-9_]+)"/g)) {
  const [, type, field] = m;
  fields.push(`    internal ${shapes.has(type) ? 'Windows.UI.Xaml.Shapes.' : ''}${type} ${field} = null!;`);
}
const code = `// Generated from ${xamlPath.split(/[\\/]/).pop()} by tools/XamlCheck/stub.mjs. Not part of the app.
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
namespace ${ns};
public sealed partial class ${name}
{
${fields.join('\n')}
    void InitializeComponent() { }
}
`;
fs.mkdirSync(outPath.replace(/[\\/][^\\/]*$/, ''), { recursive: true });
fs.writeFileSync(outPath, code);
