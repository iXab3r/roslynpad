using System;
using System.Collections.Generic;
using System.Windows.Media;
using RoslynPad.Roslyn.Completion;
using RoslynPad.Roslyn.Resources;

namespace RoslynPad.Roslyn
{
    public static class GlyphExtensions
    {
        private static readonly GlyphService _service = new();

        public static ImageSource? ToImageSource(this Glyph glyph) => _service.GetGlyphImage(glyph);

        private class GlyphService
        {
            [ThreadStatic] private static Glyphs? _glyphs;
            private static Glyphs Glyphs => _glyphs ??= new Glyphs();
            
            public GlyphService()
            {
            }

            public ImageSource? GetGlyphImage(Glyph glyph) => Glyphs[glyph] as ImageSource;
        }
    }
}
