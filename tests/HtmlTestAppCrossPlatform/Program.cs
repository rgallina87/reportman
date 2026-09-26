using System;
using Icu;

namespace HtmlTestApp
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                Wrapper.Init();
                var bidi = new BiDi();
                string text = "hello world אבג";
                bidi.SetPara(text, 254, null);
                
                Console.WriteLine("Logical Runs:");
                int start = 0;
                while (start < text.Length)
                {
                    var run = bidi.GetLogicalRun(start);
                    Console.WriteLine($"Start={run.Start}, Length={run.Length}, Level={run.Level}, Direction={run.Direction}");
                    start += run.Length;
                }
                
                Console.WriteLine("\nVisual Runs:");
                int count = bidi.CountRuns();
                for (int i = 0; i < count; i++)
                {
                    var run = bidi.GetVisualRun(i);
                    Console.WriteLine($"Start={run.Start}, Length={run.Length}, Direction={run.Direction}");
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine("Error: " + ex);
            }
        }
    }
}
