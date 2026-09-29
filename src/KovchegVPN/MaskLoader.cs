using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace KovchegVPN;

// Мини-загрузчик Wavefront OBJ+MTL для маски анонимуса.
// Понимает v/vt/vn/f/usemtl + mtl: newmtl/Kd/map_Kd. Двусторонние материалы:
// модель помечена doubleSided, нам тоже так проще (страховка от порядка обхода).
public static class MaskLoader
{
    private sealed class Face
    {
        public int[] V = new int[3];
        public int[] T = new int[3];
        public int[] N = new int[3];
        public string Mat = "";
    }

    public static Model3DGroup Load(Stream objStream, Stream mtlStream, Func<string, Stream> openTex)
    {
        var mats = ParseMtl(mtlStream, openTex);
        using var sr = new StreamReader(objStream);
        var pos = new List<Point3D>();
        var uvs = new List<Point>();
        var nrm = new List<Vector3D>();
        var faces = new List<Face>();
        string cur = "";
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0])
            {
                case "v":
                    pos.Add(new Point3D(F(p[1]), F(p[2]), F(p[3])));
                    break;
                case "vt":
                    uvs.Add(new Point(F(p[1]), F(p[2])));
                    break;
                case "vn":
                    nrm.Add(new Vector3D(F(p[1]), F(p[2]), F(p[3])));
                    break;
                case "usemtl":
                    cur = p.Length > 1 ? p[1] : "";
                    break;
                case "f":
                    // Триангуляция веером (конвертер и так даёт треугольники).
                    for (int i = 2; i < p.Length - 1; i++)
                        faces.Add(MkFace(p[1], p[i], p[i + 1], cur));
                    break;
            }
        }

        var group = new Model3DGroup();
        foreach (var g in faces.GroupBy(f => f.Mat))
        {
            var mesh = new MeshGeometry3D();
            var cache = new Dictionary<(int v, int t, int n), int>();
            foreach (var f in g)
            {
                if (f.V[0] < 0 || f.V[0] >= pos.Count ||
                    f.V[1] < 0 || f.V[1] >= pos.Count ||
                    f.V[2] < 0 || f.V[2] >= pos.Count)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    var key = (f.V[k], f.T[k], f.N[k]);
                    if (!cache.TryGetValue(key, out int idx))
                    {
                        idx = mesh.Positions.Count;
                        cache[key] = idx;
                        mesh.Positions.Add(pos[f.V[k]]);
                        mesh.TextureCoordinates.Add(f.T[k] >= 0 && f.T[k] < uvs.Count ? uvs[f.T[k]] : default);
                        mesh.Normals.Add(f.N[k] >= 0 && f.N[k] < nrm.Count ? nrm[f.N[k]] : default);
                    }
                    mesh.TriangleIndices.Add(idx);
                }
            }
            if (mesh.Positions.Count == 0) continue;
            var mat = mats.TryGetValue(g.Key, out var m) ? m : DefaultMat();
            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }
        Fit(group);
        return group;
    }

    // Центрировать и вписать большей стороной в [-1, 1].
    private static void Fit(Model3DGroup group)
    {
        var b = Rect3D.Empty;
        foreach (var gm in group.Children.OfType<GeometryModel3D>())
            if (gm.Geometry is MeshGeometry3D m)
                b.Union(m.Bounds);
        if (b.IsEmpty) return;
        double k = 2.0 / Math.Max(b.SizeY, Math.Max(b.SizeX, b.SizeZ));
        if (!double.IsFinite(k) || k <= 0) return;
        var c = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        var t = new Transform3DGroup();
        t.Children.Add(new TranslateTransform3D(-c.X, -c.Y, -c.Z));
        t.Children.Add(new ScaleTransform3D(k, k, k));
        group.Transform = t;
    }

    private static Dictionary<string, Material> ParseMtl(Stream s, Func<string, Stream> openTex)
    {
        var mats = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        string cur = "";
        double r = 0.8, g = 0.8, b = 0.8;
        string? tex = null;
        void flush()
        {
            if (cur.Length == 0) return;
            Material m;
            if (tex != null)
            {
                try
                {
                    using var ts = openTex(tex);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ts;
                    bmp.EndInit();
                    bmp.Freeze();
                    // Лицо почти чёрное — капля собственного свечения, чтобы не было дырой.
                    m = new MaterialGroup
                    {
                        Children =
                        {
                            new DiffuseMaterial(new ImageBrush(bmp)),
                            new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x0C))),
                        },
                    };
                }
                catch { m = Solid(r, g, b); }
            }
            else
            {
                // Золото: блик от точечного света.
                m = new MaterialGroup
                {
                    Children =
                    {
                        new DiffuseMaterial(new SolidColorBrush(Sc(r, g, b))),
                        new SpecularMaterial(new SolidColorBrush(Colors.White), 24),
                    },
                };
            }
            mats[cur] = m;
        }
        using var sr = new StreamReader(s);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p[0].StartsWith('#')) continue;
            switch (p[0].ToLowerInvariant())
            {
                case "newmtl":
                    flush();
                    cur = p.Length > 1 ? p[1] : "";
                    r = g = b = 0.8;
                    tex = null;
                    break;
                case "kd":
                    if (p.Length >= 4) { r = F(p[1]); g = F(p[2]); b = F(p[3]); }
                    break;
                case "map_kd":
                    tex = p[^1];
                    break;
            }
        }
        flush();
        return mats;
    }

    private static Material Solid(double r, double g, double b) =>
        new DiffuseMaterial(new SolidColorBrush(Sc(r, g, b)));

    private static Color Sc(double r, double g, double b) =>
        Color.FromScRgb(1, (float)r, (float)g, (float)b);

    private static Material DefaultMat() =>
        new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));

    private static Face MkFace(string a, string b, string c, string mat)
    {
        var f = new Face { Mat = mat };
        var vs = new[] { a, b, c };
        for (int i = 0; i < 3; i++)
        {
            var t = vs[i].Split('/');
            f.V[i] = Idx(t, 0);
            f.T[i] = Idx(t, 1);
            f.N[i] = Idx(t, 2);
        }
        return f;
    }

    private static int Idx(string[] t, int i) =>
        t.Length > i && int.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v - 1 : -1;

    private static double F(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
