using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using SourcePawn;

namespace Lysis
{
    // The analyzer is recursive rather than iterative. We don't add
    // expression names to a block's node list because we only want to emit
    // statements. This makes things kind of awkward for analysis, but oh
    // well.
    class NodeAnalyzer
    {
        private SourcePawnFile file_;
        private NodeBlock[] blocks_;

        private void analyzeLocal(DDeclareLocal local)
        {
            Variable var = file_.lookupVariable(local.pc, local.offset);
            
        }

        public NodeAnalyzer(SourcePawnFile file, NodeBlock[] blocks)
        {
            file_ = file;
            blocks_ = blocks;
        }

        public void analyze()
        {
            for (int i = blocks_.Length - 1; i >= 0; i--)
                analyzeDeclarations(blocks_[i]);

            for (int i = blocks_.Length - 1; i >= 0; i--)
                rewriteStatements(blocks_[i]);
        }

        private DNode transform(DConstant node, Tag tag, VariableType type)
        {
            switch (type)
            {
                case VariableType.Array:
                case VariableType.ArrayReference:
                case VariableType.Reference:
                case VariableType.Variadic:
                {
                    Variable global = file_.lookupGlobal(node.value);
                    if (global != null)
                        return new DGlobal(global);

                    if (tag.name == "String")
                        return new DString(file_.stringFromData(node.value));

                    return node;
                }
            }

            return node;
        }

        private DNode applyType(DNode node, Argument arg)
        {
            switch (node.type)
            {
                case NodeType.Constant:
                    return transform((DConstant)node, arg.tag, arg.type);
            }

            return node;
        }

        private void rewriteSysReq(DSysReq sysreq)
        {
            Native native = sysreq.native;

            for (int i = 0; i < sysreq.numOperands && i < native.args.Length; i++)
            {
                DNode node = sysreq.getOperand(i);
                Argument arg = i < native.args.Length
                               ? native.args[i]
                               : native.args[native.args.Length - 1];

                DNode replacement = applyType(node, arg);
                if (node != replacement)
                    sysreq.replaceOperand(i, replacement);
            }
        }

        // Operators can be overloaded for floats, and we want these to print
        // normally, so here is some gross peephole stuff. Maybe we should be
        // looking for bytecode patterns instead or something, but that would
        // need a whole-program analysis.
        private DNode rewriteOperator(DCall call)
        {
            if (call.function.name.Length < 8)
                return call;

            if (call.function.name.Substring(0, 8) != "operator")
                return call;

            string op = "";
            for (int i = 8; i < call.function.name.Length; i++)
            {
                if (call.function.name[i] == '(')
                    break;
                op += call.function.name[i];
            }

            SPOpcode spop;
            switch (op)
            {
                case ">":
                    spop = SPOpcode.sgrtr;
                    break;
                case ">=":
                    spop = SPOpcode.sgeq;
                    break;
                case "<":
                    spop = SPOpcode.sless;
                    break;
                case "<=":
                    spop = SPOpcode.sleq;
                    break;
                default:
                    throw new Exception("unknown operator");
            }

            switch (spop)
            {
                case SPOpcode.sgeq:
                case SPOpcode.sleq:
                case SPOpcode.sgrtr:
                case SPOpcode.sless:
                {
                    if (call.numOperands != 2)
                        return call;
                    Debug.Assert(call.uses.Count == 1);
                    DNode lhs = rewriteExpression(call.getOperand(0));
                    DNode rhs = rewriteExpression(call.getOperand(1));
                    return new DBinary(spop, lhs, rhs);
                }
                default:
                    throw new Exception("unknown spop");
            }
        }

        private DNode rewriteCall(DCall call)
        {
            return rewriteOperator(call);
        }

        private DNode rewriteExpression(DNode node)
        {
            switch (node.type)
            {
                case NodeType.Call:
                    return rewriteCall((DCall)node);
            }
            return node;
        }

        private DNode rewriteOperand(DNode node, int i)
        {
            DNode op = node.getOperand(i);
            DNode rewrite = rewriteExpression(op);
            if (rewrite != op)
                node.replaceOperand(i, rewrite);
            return rewrite;
        }

        private void rewriteJump(DJumpCondition jump)
        {
            if (jump.getOperand(0) == null)
                return;

            DNode lhs = rewriteOperand(jump, 0);
            DNode rhs = rewriteOperand(jump, 1);

            Debug.Assert(lhs != rhs);

            if (lhs.type == NodeType.Binary)
            {
                if (rhs.type != NodeType.Constant)
                    return;

                DConstant bit = (DConstant)rhs;
                if (bit.value != 1 && bit.value != 0)
                    return;

                bool onTrue;
                switch (jump.spop)
                {
                    case SPOpcode.jzer:
                        onTrue = (bit.value == 1);
                        break;
                    case SPOpcode.jnz:
                        onTrue = (bit.value == 0);
                        break;
                    default:
                        throw new Exception("unknown?");
                }

                SPOpcode newop;
                DBinary cmp = (DBinary)lhs;
                switch (cmp.spop)
                {
                    case SPOpcode.sleq:
                        newop = onTrue ? SPOpcode.jsleq : SPOpcode.jsgrtr;
                        break;
                    case SPOpcode.sless:
                        newop = onTrue ? SPOpcode.jsless : SPOpcode.jsgeq;
                        break;
                    case SPOpcode.sgrtr:
                        newop = onTrue ? SPOpcode.jsgrtr : SPOpcode.jsleq;
                        break;
                    case SPOpcode.sgeq:
                        newop = onTrue ? SPOpcode.jsgeq : SPOpcode.jsless;
                        break;
                    case SPOpcode.eq:
                        newop = onTrue ? SPOpcode.jeq : SPOpcode.jneq;
                        break;
                    case SPOpcode.neq:
                        newop = onTrue ? SPOpcode.neq : SPOpcode.eq;
                        break;
                    default:
                        return;
                }

                jump.rewrite(newop, cmp.getOperand(0), cmp.getOperand(1));
            }
        }

        private void rewriteStore(DStore store)
        {

        }

        private void rewriteStatements(NodeBlock block)
        {
            KList<DNode>.reverse_iterator iter = block.nodes.rbegin();
            while (iter.more())
            {
                DNode node = iter.value;
                switch (node.type)
                {
                    case NodeType.SysReq:
                        rewriteSysReq((DSysReq)node);
                        break;

                    case NodeType.JumpCondition:
                        rewriteJump((DJumpCondition)node);
                        break;

                    case NodeType.Store:
                        rewriteStore((DStore)node);
                        break;
                }
                iter.next();
            }
        }

        private void analyzeDeclarations(NodeBlock block)
        {
            KList<DNode>.reverse_iterator iter = block.nodes.rbegin();
            while (iter.more())
            {
                bool remove = false;
                DNode node = iter.value;
                switch (node.type)
                {
                    case NodeType.DeclareLocal:
                    {
                        DDeclareLocal decl = (DDeclareLocal)node;
                        Variable var = file_.lookupVariable(decl.pc, decl.offset);
                        if (var == null)
                        {
                            Debug.Assert(decl.uses.Count <= 1);
                            if (decl.uses.Count <= 1)
                            {
                                remove = true;

                                // This was probably just a stack temporary.
                                if (decl.uses.Count == 1)
                                {
                                    DUse use = decl.uses.First.Value;
                                    use.node.replaceOperand(use.index, decl.value);
                                }
                            }
                        }
                        else
                        {
                            decl.setVariable(var);
                        }
                        break;
                    }

                    case NodeType.Call:
                    case NodeType.SysReq:
                    {
                        // Calls are not necessarily idempotent. No idea if
                        // this will be correct, but we need to stop them from
                        // being emitted twice. This code hopes that (1) the
                        // call will be reached from some other instruction,
                        // and that (2) emitting it there will preserve
                        // semantics.
                        if (node.uses.Count > 0)
                            remove = true;
                        break;
                    }
                }
                if (remove)
                    block.nodes.remove(iter);
                else
                    iter.next();
            }
        }
    }
}
