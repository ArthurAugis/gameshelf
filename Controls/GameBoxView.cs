using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GameShelf.Models;
using GameShelf.Services;
using GameShelf.Theming;

namespace GameShelf.Controls;

/// <summary>
/// A 3D game box with rounded edges: cover on the front, shelf spine on the left, a printed back, top, bottom
/// and opposite side. Drag to rotate, mouse wheel to zoom.
/// </summary>
internal sealed class GameBoxView : Grid
{
    // Cover proportions (600x900). The depth follows the spine's aspect ratio so the side matches the shelf.
    const double BoxWidth = 2, BoxHeight = 3;
    const double EdgeRadius = 0.06;
    const int EdgeSegments = 6; // mesh subdivisions of each rounded edge

    const double DragYawPerPixel = 0.8, DragPitchPerPixel = 0.5, MaxPitch = 60;
    const double MinCameraDistance = 3.5, MaxCameraDistance = 9, WheelUnitsPerNotch = 240;
    const double TextureScale = 2; // texture pixels per element pixel

    readonly Game game;
    readonly Color[] colors;
    readonly DiffuseMaterial backMaterial = new();
    readonly AxisAngleRotation3D yaw = new(new Vector3D(0, 1, 0), -30);
    readonly AxisAngleRotation3D pitch = new(new Vector3D(1, 0, 0), 8);
    readonly PerspectiveCamera camera = new(new Point3D(0, 0, 5.2), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 40);
    Point? dragFrom;

    public GameBoxView(Game game, Color color)
    {
        this.game = game;
        colors = game.Cover is { } coverPath
            ? ColorUtil.Palette(coverPath)
            : new[] { ColorUtil.Shade(color, 1.15), color, ColorUtil.Shade(color, 0.7) };

        Background = Brushes.Transparent; // a null background would not receive mouse events
        ClipToBounds = true;
        Cursor = Cursors.SizeAll;
        Children.Add(CreateViewport());
    }

    /// <summary>Redraws the back cover once the store details and the artwork banner are known.</summary>
    public void UpdateBack(GameDetails? details, string? heroPath) =>
        backMaterial.Brush = BoxTextures.Rasterize(BoxTextures.Back(game, colors, details, heroPath), TextureScale);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        dragFrom = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragFrom is not { } from) return;
        var position = e.GetPosition(this);
        yaw.Angle += (position.X - from.X) * DragYawPerPixel;
        pitch.Angle = Math.Clamp(pitch.Angle + (position.Y - from.Y) * DragPitchPerPixel, -MaxPitch, MaxPitch);
        dragFrom = position;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e) =>
        camera.Position = new Point3D(0, 0,
            Math.Clamp(camera.Position.Z - e.Delta / WheelUnitsPerNotch, MinCameraDistance, MaxCameraDistance));

    Viewport3D CreateViewport()
    {
        var spine = new SpineView(game, colors[1]) { Margin = new Thickness(0) };
        spine.SetPalette(colors);
        var half = new Vector3D(BoxWidth / 2, BoxHeight / 2, BoxHeight * spine.Width / spine.Height / 2);

        FrameworkElement frontFace = game.Cover is { } coverPath
            ? new Image { Source = ImageLoader.Load(coverPath), Stretch = Stretch.Fill, Width = 300, Height = 450 }
            : BoxTextures.TitleCard(game, colors);
        Brush front = BoxTextures.Rasterize(BoxTextures.WithPlatforms(frontFace, game), TextureScale);
        UpdateBack(null, null);

        // Each face: outward normal, then the directions of the texture's right and up as seen from outside.
        var box = new Model3DGroup();
        box.Children.Add(Face(new(0, 0, 1), new(1, 0, 0), new(0, 1, 0), half, new DiffuseMaterial(front)));
        box.Children.Add(Face(new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0), half, backMaterial));
        box.Children.Add(Face(new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0), half,
            new DiffuseMaterial(BoxTextures.Rasterize(spine, 3))));
        box.Children.Add(Face(new(1, 0, 0), new(0, 0, -1), new(0, 1, 0), half,
            new DiffuseMaterial(BoxTextures.Rasterize(BoxTextures.Side(game, colors), 3))));
        box.Children.Add(Face(new(0, 1, 0), new(1, 0, 0), new(0, 0, -1), half,
            new DiffuseMaterial(BoxTextures.Rasterize(BoxTextures.Top(game, colors), TextureScale))));
        box.Children.Add(Face(new(0, -1, 0), new(1, 0, 0), new(0, 0, 1), half,
            new DiffuseMaterial(BoxTextures.Rasterize(BoxTextures.Bottom(colors), TextureScale))));
        box.Transform = new Transform3DGroup
        {
            Children = { new RotateTransform3D(yaw), new RotateTransform3D(pitch) },
        };

        var viewport = new Viewport3D { Camera = camera, IsHitTestVisible = false };
        viewport.Children.Add(new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                {
                    new AmbientLight(Color.FromRgb(110, 110, 110)),
                    new DirectionalLight(Color.FromRgb(150, 150, 150), new Vector3D(-0.3, -0.4, -1)),
                    new DirectionalLight(Color.FromRgb(70, 70, 70), new Vector3D(0.6, -0.7, -0.4)),
                    box,
                },
            },
        });
        return viewport;
    }

    /// <summary>One face of the rounded box, with a plastic-like highlight on top of its texture.</summary>
    static GeometryModel3D Face(Vector3D normal, Vector3D right, Vector3D up, Vector3D half, DiffuseMaterial texture)
    {
        var material = new MaterialGroup
        {
            Children = { texture, new SpecularMaterial(new SolidColorBrush(Color.FromRgb(70, 70, 70)), 40) },
        };
        return new GeometryModel3D(RoundedFace(normal, right, up, half), material) { BackMaterial = material };
    }

    /// <summary>
    /// Mesh of one face of a box whose edges are rounded. A grid is laid on the flat face; every point is then
    /// pushed onto the rounded surface: clamp it into the inner box (the box shrunk by the radius), and place it
    /// one radius away from there, in the direction it was from the inner box. The texture still maps the flat face.
    /// </summary>
    static MeshGeometry3D RoundedFace(Vector3D normal, Vector3D right, Vector3D up, Vector3D half)
    {
        double halfN = HalfAlong(normal, half), halfU = HalfAlong(right, half), halfV = HalfAlong(up, half);
        double[] us = Samples(halfU), vs = Samples(halfV);
        var inner = new Vector3D(half.X - EdgeRadius, half.Y - EdgeRadius, half.Z - EdgeRadius);

        var mesh = new MeshGeometry3D();
        foreach (var v in vs)
            foreach (var u in us)
            {
                var flat = normal * halfN + right * u + up * v;
                var core = new Vector3D(
                    Math.Clamp(flat.X, -inner.X, inner.X),
                    Math.Clamp(flat.Y, -inner.Y, inner.Y),
                    Math.Clamp(flat.Z, -inner.Z, inner.Z));
                var outward = flat - core;
                outward.Normalize();
                var position = core + outward * EdgeRadius;

                mesh.Positions.Add(new Point3D(position.X, position.Y, position.Z));
                mesh.Normals.Add(outward);
                mesh.TextureCoordinates.Add(new Point((u + halfU) / (2 * halfU), 1 - (v + halfV) / (2 * halfV)));
            }

        int width = us.Length;
        for (int j = 0; j < vs.Length - 1; j++)
            for (int i = 0; i < width - 1; i++)
            {
                int bottomLeft = j * width + i, bottomRight = bottomLeft + 1;
                int topLeft = bottomLeft + width, topRight = topLeft + 1;
                foreach (var index in new[] { bottomLeft, bottomRight, topRight, bottomLeft, topRight, topLeft })
                    mesh.TriangleIndices.Add(index);
            }
        return mesh;
    }

    /// <summary>Half the box size along an axis direction (a unit vector along x, y or z).</summary>
    static double HalfAlong(Vector3D axis, Vector3D half) =>
        Math.Abs(axis.X) * half.X + Math.Abs(axis.Y) * half.Y + Math.Abs(axis.Z) * half.Z;

    /// <summary>Grid positions along one axis: dense inside the two rounded borders, one big step across the flat middle.</summary>
    static double[] Samples(double halfSize)
    {
        var samples = new List<double>();
        for (int i = 0; i <= EdgeSegments; i++) samples.Add(-halfSize + EdgeRadius * i / EdgeSegments);
        for (int i = 0; i <= EdgeSegments; i++) samples.Add(halfSize - EdgeRadius + EdgeRadius * i / EdgeSegments);
        return samples.ToArray();
    }
}
