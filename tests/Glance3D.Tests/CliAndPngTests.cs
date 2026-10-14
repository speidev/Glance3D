using StbImageSharp;
using Xunit;

namespace Glance3D.Tests
{
    public class CliTests
    {
        [Fact]
        public void Parses_SizeViewAndGrid()
        {
            Assert.True(Program.TryParse(["--size", "640x480", "--view", "-90,-15.5", "--no-grid"], out var o, out _));
            Assert.NotNull(o);
            Assert.Equal(640, o.Width);
            Assert.Equal(480, o.Height);
            Assert.Equal(-90f, o.YawDeg);
            Assert.Equal(-15.5f, o.PitchDeg);
            Assert.False(o.ShowGrid);
        }

        [Fact]
        public void Help_ReturnsNoOptions()
        {
            Assert.True(Program.TryParse(["--help"], out var o, out _));
            Assert.Null(o);
        }

        [Theory]
        [InlineData("--size", "big")]
        [InlineData("--view", "10")]
        [InlineData("--frobnicate")]
        [InlineData("no-such-file.glb")]
        [InlineData("--capture")]
        public void Rejects_BadInput(params string[] args)
        {
            Assert.False(Program.TryParse(args, out _, out var error));
            Assert.NotEmpty(error);
        }

        [Fact]
        public void Capture_RequiresAModel()
        {
            Assert.False(Program.TryParse(["--capture", "out.png"], out _, out var error));
            Assert.Contains("model", error);
        }
    }

    public class PngWriterTests
    {
        [Fact]
        public void WrittenPng_DecodesToTheSamePixels_FlippedFromGlOrder()
        {
            // 2x2, bottom row first (GL readback order): red, green / blue, white.
            byte[] gl = [255, 0, 0, 255,   0, 255, 0, 255,
                         0, 0, 255, 255,   255, 255, 255, 128];
            string path = Path.Combine(Path.GetTempPath(), $"glance3d-{Guid.NewGuid():N}.png");
            try
            {
                PngWriter.Write(path, 2, 2, gl, flipVertically: true);
                using var s = File.OpenRead(path);
                var img = ImageResult.FromStream(s, ColorComponents.RedGreenBlueAlpha);

                Assert.Equal(2, img.Width);
                // Top-left of the PNG is the first pixel of GL's top (second) row: blue.
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, img.Data[..4]);
                Assert.Equal(128, img.Data[7]);
                Assert.Equal(new byte[] { 255, 0, 0, 255 }, img.Data[8..12]);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
