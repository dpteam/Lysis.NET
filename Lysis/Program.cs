using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SourcePawn;

namespace Lysis
{
    class Program
    {
        static void DumpMethod(SourcePawnFile file, SourceBuilder source, uint addr)
        {
            MethodParser mp = new MethodParser(file, addr);
            LGraph graph = mp.parse();
            //DebugSpew.DumpGraph(graph.blocks, System.Console.Out);

            NodeBuilder nb = new NodeBuilder(file, graph);
            NodeBlock[] nblocks = nb.buildNodes();

            NodeGraph ngraph = new NodeGraph(file, nblocks);

            // Remove dead phis first.
            NodeAnalysis.RemoveDeadCode(ngraph);
            
            NodeRewriter rewriter = new NodeRewriter(ngraph);
            rewriter.rewrite();

            NodeAnalysis.CollapseArrayReferences(ngraph);

            // Propagate type information.
            ForwardTypePropagation ftypes = new ForwardTypePropagation(ngraph);
            ftypes.propagate();

            BackwardTypePropagation btypes = new BackwardTypePropagation(ngraph);
            btypes.propagate();

            // We're not fixpoint, so just iterate again.
            ftypes.propagate();
            btypes.propagate();

            // Try this again now that we have type information.
            NodeAnalysis.CollapseArrayReferences(ngraph);

            ftypes.propagate();
            btypes.propagate();

            // Coalesce x[y] = x[y] + 5 into x[y] += 5
            NodeAnalysis.CoalesceLoadStores(ngraph);

            // After this, it is not legal to run type analysis again, because
            // arguments expecting references may have been rewritten to take
            // constants, for pretty-printing.
            NodeAnalysis.AnalyzeHeapUsage(ngraph);

            // Do another DCE pass, this time, without guards.
            NodeAnalysis.RemoveGuards(ngraph);
            NodeAnalysis.RemoveDeadCode(ngraph);

            NodeRenamer renamer = new NodeRenamer(ngraph);
            renamer.rename();

            // Do a pass to coalesce declaration+stores.
            NodeAnalysis.CoalesceLoadsAndDeclarations(ngraph);

            // Simplify conditional expressions.
            // BlockAnalysis.NormalizeConditionals(ngraph);
            var sb = new SourceStructureBuilder(ngraph);
            var structure = sb.build();

            source.write(structure);

            //System.Console.In.Read();
            //System.Console.In.Read();
        }

        static Function FunctionByName(SourcePawnFile file, string name)
        {
            for (int i = 0; i < file.functions.Length; i++)
            {
                if (file.functions[i].name == name)
                    return file.functions[i];
            }
            return null;
        }

        static void Main(string[] args)
        {
            // 2020 Additions...
            Marshal.PrelinkAll(typeof(Program));

            if (args.Length < 1)
            {
                System.Console.Error.Write("usage: <file.smx> or <file.amxx>");
                return;
            }
            
            string path = args[0];
            PawnFile file = PawnFile.FromFile(path);
            
            SourceBuilder source = new SourceBuilder(file, System.Console.Out);
            source.writeGlobals();

            for (int i = 0; i < file.functions.Length; i++)
            {
                Function fun = file.functions[i];
//#if 
                try
                {
                    DumpMethod((SourcePawnFile)file, source, fun.address);
                    System.Console.WriteLine("");
                }
                catch (Exception e)
                {
                    System.Console.WriteLine("");
                    System.Console.WriteLine("/* ERROR! " + e.Message + " */");
                    System.Console.WriteLine(" function \"" + fun.name + "\" (number " + i + ")");
                    source = new SourceBuilder((SourcePawnFile)file, Console.Out);
                }
//#endif
            }

        }
    }
}
