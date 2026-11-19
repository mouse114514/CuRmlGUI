using System;
using System.Linq;
using System.Reflection;
using SkiaSharp;

namespace CuRmlGUI.RenderTest
{
    internal static class ReflectDump
    {
        public static void Run()
        {
            Console.WriteLine("=== SKVertices.CreateCopy ===");
            foreach (var m in typeof(SKVertices).GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.Name.StartsWith("CreateCopy")))
                Console.WriteLine("  " + Sig(m));

            Console.WriteLine("=== SKCanvas.DrawVertices ===");
            foreach (var m in typeof(SKCanvas).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => m.Name == "DrawVertices"))
                Console.WriteLine("  " + Sig(m));

            Console.WriteLine("=== SKImage.ToShader ===");
            foreach (var m in typeof(SKImage).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => m.Name == "ToShader"))
                Console.WriteLine("  " + Sig(m));

            Console.WriteLine("=== SKShader.CreateImage ===");
            foreach (var m in typeof(SKShader).GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.Name == "CreateImage"))
                Console.WriteLine("  " + Sig(m));

            Console.WriteLine("=== SKVertices props ===");
            foreach (var p in typeof(SKVertices).GetProperties())
                Console.WriteLine("  " + p.PropertyType.Name + " " + p.Name);
        }

        private static string Sig(MethodInfo m) =>
            m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
    }
}
