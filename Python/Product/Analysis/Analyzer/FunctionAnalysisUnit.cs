// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.PythonTools.Analysis.Values;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.Analyzer {
    class FunctionAnalysisUnit : AnalysisUnit {
        public readonly FunctionInfo Function;

        internal readonly AnalysisUnit _declUnit;
        private readonly Dictionary<Node, Expression> _decoratorCalls;

        internal FunctionAnalysisUnit(
            FunctionInfo function,
            AnalysisUnit declUnit,
            InterpreterScope declScope,
            IPythonProjectEntry declEntry
        )
            : base(function.FunctionDefinition, null) {
            _declUnit = declUnit;
            Function = function;
            _decoratorCalls = new Dictionary<Node, Expression>();

            var scope = new FunctionScope(Function, Function.FunctionDefinition, declScope, declEntry);
            _scope = scope;

            AnalysisLog.NewUnit(this);
        }

        internal FunctionAnalysisUnit(FunctionInfo function, AnalysisUnit declUnit) : base(function.FunctionDefinition, null)  {
            _declUnit = declUnit;
            Function = function;
        }

        internal virtual void EnsureParameters() {
            ((FunctionScope)Scope).EnsureParameters(this, usePlaceholders: true);
        }

        internal virtual bool UpdateParameters(ArgumentSet callArgs, bool enqueue = true) {
            return ((FunctionScope)Scope).UpdateParameters(this, callArgs, enqueue, null);
        }

        internal void AddNamedParameterReferences(AnalysisUnit caller, NameExpression[] names) {
            ((FunctionScope)Scope).AddParameterReferences(caller, names);
        }

        internal override ModuleInfo GetDeclaringModule() {
            return base.GetDeclaringModule() ?? _declUnit.DeclaringModule;
        }

        internal override void AnalyzeWorker(DDG ddg, CancellationToken cancel) {
            EnsureParameters();

            // Resolve default parameters and decorators in the outer scope but
            // continue to associate changes with this unit.
            ddg.Scope = _declUnit.Scope;
            AnalyzeDefaultParameters(ddg);

            var funcType = ProcessFunctionDecorators(ddg);

            var v = ddg.Scope.AddLocatedVariable(Ast.Name, Ast.NameExpression, this);
            v.AddAssignment(new EncodedLocation(this, Ast), ProjectEntry);

            // Set the scope to within the function
            ddg.Scope = Scope;

            Ast.Body.Walk(ddg);

            v.AddTypes(this, funcType);
        }


        public new FunctionDefinition Ast {
            get {
                return (FunctionDefinition)base.Ast;
            }
        }

        public VariableDef ReturnValue {
            get {
                return ((FunctionScope)Scope).ReturnValue;
            }
        }

        internal IAnalysisSet ProcessFunctionDecorators(DDG ddg) {
            var types = Function.SelfSet;
            if (Ast.Decorators != null) {
                Expression expr = Ast.NameExpression;

                foreach (var d in Ast.Decorators.Decorators) {
                    if (d != null) {
                        var decorator = ddg._eval.Evaluate(d);

                        if (decorator.Contains(State.ClassInfos[BuiltinTypeId.Property])) {
                            Function.IsProperty = true;
                        } else if (decorator.Contains(State.ClassInfos[BuiltinTypeId.StaticMethod])) {
                            // TODO: Warn if IsClassMethod is set
                            Function.IsStatic = true;
                        } else if (decorator.Contains(State.ClassInfos[BuiltinTypeId.ClassMethod])) {
                            // TODO: Warn if IsStatic is set
                            Function.IsClassMethod = true;
                        } else {
                            Expression nextExpr;
                            if (!_decoratorCalls.TryGetValue(d, out nextExpr)) {
                                nextExpr = _decoratorCalls[d] = new CallExpression(d, new[] { new Arg(expr) });
                            }
                            expr = nextExpr;
                            var decorated = AnalysisSet.Empty;
                            foreach (var ns in decorator) {
                                var fd = ns as FunctionInfo;
                                if (fd != null && Scope.EnumerateTowardsGlobal.Any(s => s.AnalysisValue == fd)) {
                                    continue;
                                }
                                decorated = decorated.Union(ns.Call(expr, this, new[] { types }, ExpressionEvaluator.EmptyNames));
                            }

                            // If processing decorators, update the current
                            // function type. Otherwise, we are acting as if
                            // each decorator returns the function unmodified.
                            if (ddg.ProjectState.Limits.ProcessCustomDecorators) {
                                types = decorated;
                            }
                        }
                    }
                }
            }

            if (!Function.IsStatic && Ast.Parameters.Count > 0) {
                VariableDef param;
                IAnalysisSet firstParam;
                var clsScope = ddg.Scope as ClassScope;
                if (clsScope == null) {
                    firstParam = Function.IsClassMethod ? State.ClassInfos[BuiltinTypeId.Type].SelfSet : AnalysisSet.Empty;
                } else {
                    firstParam = Function.IsClassMethod ? clsScope.Class.SelfSet : clsScope.Class.Instance.SelfSet;
                }

                if (Scope.TryGetVariable(Ast.Parameters[0].Name, out param)) {
                    param.AddTypes(this, firstParam, false);
                }
            }

            return types;
        }

        internal void AnalyzeDefaultParameters(DDG ddg) {
            VariableDef param;
            for (int i = 0; i < Ast.Parameters.Count; ++i) {
                var p = Ast.Parameters[i];
                if (p.Annotation != null) {
                    var val = ddg._eval.EvaluateAnnotation(p.Annotation).GetInstanceType();
                    if (val?.Any() == true && Scope.TryGetVariable(p.Name, out param)) {
                        param.AddTypes(this, val, false);
                    }
                }

                if (p.DefaultValue != null && p.Kind != ParameterKind.List && p.Kind != ParameterKind.Dictionary &&
                    Scope.TryGetVariable(p.Name, out param)) {
                    var val = ddg._eval.Evaluate(p.DefaultValue);
                    if (val != null) {
                        param.AddTypes(this, val, false);
                    }
                }
            }
            if (Ast.ReturnAnnotation != null) {
                var ann = ddg._eval.EvaluateAnnotation(Ast.ReturnAnnotation);
                var resType = AnalysisSet.Empty;
                if (Ast.IsGenerator && ann.Split<GeneratorInfo>(out var gens, out resType)) {
                    var gen = ((FunctionScope)Scope).Generator;
                    foreach (var g in gens) {
                        g.Yields.CopyTo(gen.Yields);
                        g.Sends.CopyTo(gen.Sends);
                        g.Returns.CopyTo(gen.Returns);
                    }
                }

                ((FunctionScope)Scope).AddReturnTypes(
                    Ast.ReturnAnnotation,
                    ddg._unit,
                    resType.GetInstanceType()
                );
            }
        }

        public override string ToString() {
            return string.Format("{0}{1}({2})->{3}",
                base.ToString(),
                " def:",
                string.Join(", ", Ast.Parameters.Select(p => Scope.GetVariable(p.Name).TypesNoCopy.ToString())),
                ((FunctionScope)Scope).ReturnValue.TypesNoCopy.ToString()
            );
        }
    }

    class FunctionClosureAnalysisUnit : FunctionAnalysisUnit {
        private readonly FunctionAnalysisUnit _inner;

        internal FunctionClosureAnalysisUnit(FunctionAnalysisUnit inner) :
            base(inner.Function, inner._declUnit, inner._declUnit.Scope, inner.ProjectEntry) {
            _inner = inner;
            ((FunctionScope)_inner.Scope).AddLinkedScope((FunctionScope)Scope);
        }

        internal override void EnsureParameters() {
            ((FunctionScope)Scope).EnsureParameters(this, usePlaceholders: false);
        }

        internal override void AnalyzeWorker(DDG ddg, CancellationToken cancel) {
            base.AnalyzeWorker(ddg, cancel);
        }
    }
}
