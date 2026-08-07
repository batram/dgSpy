/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Diagnostics;
using dnSpy.Contracts.Debugger.Evaluation;

namespace dnSpy.Debugger.Evaluation.ViewModel.Impl {
	abstract class DbgValueNodeReader {
		public abstract void SetEvaluationInfo(DbgEvaluationInfo? evalInfo);
		public abstract void SetValueNodeEvaluationOptions(DbgValueNodeEvaluationOptions options);
		public abstract DbgValueNode GetDebuggerNode(ChildDbgValueRawNode valueNode);
		public abstract DbgValueNode GetDebuggerNodeForReuse(DebuggerValueRawNode parent, uint startIndex);
		public abstract DbgValueNodeInfo Evaluate(string expression);
	}

	sealed class DbgValueNodeReaderImpl : DbgValueNodeReader {
		readonly Func<DbgEvaluationInfo, string, DbgValueNodeInfo> evaluate;
		DbgEvaluationInfo? evalInfo;
		DbgValueNodeEvaluationOptions dbgValueNodeEvaluationOptions;

		public DbgValueNodeReaderImpl(Func<DbgEvaluationInfo, string, DbgValueNodeInfo> evaluate) =>
			this.evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));

		public override void SetEvaluationInfo(DbgEvaluationInfo? evalInfo) => this.evalInfo = evalInfo;
		public override void SetValueNodeEvaluationOptions(DbgValueNodeEvaluationOptions options) => dbgValueNodeEvaluationOptions = options;

		public override DbgValueNode GetDebuggerNode(ChildDbgValueRawNode valueNode) {
			Debug2.Assert(evalInfo is not null);
			var parent = valueNode.Parent;
			uint startIndex = valueNode.DbgValueNodeChildIndex;
			return GetSingleChild(parent.DebuggerValueNode, startIndex);
		}

		public override DbgValueNode GetDebuggerNodeForReuse(DebuggerValueRawNode parent, uint startIndex) {
			Debug2.Assert(evalInfo is not null);
			return GetSingleChild(parent.DebuggerValueNode, startIndex);
		}

		// dgSpy: the two call sites above are the whole reason DbgEngineValueNodeImpl used to answer a failed
		// expansion with a page of fabricated error nodes — they ask for one child and index [0], so an empty
		// result crashed them. The engine now throws instead, because a page of placeholders is real data to
		// anything that is not a treeview (see get_members). The treeview still wants a row, so build the one
		// row here, from the message the engine would have put in it.
		DbgValueNode GetSingleChild(DbgValueNode parent, uint startIndex) {
			const int count = 1;
			try {
				var newNodes = parent.GetChildren(evalInfo!, startIndex, count, dbgValueNodeEvaluationOptions);
				Debug.Assert(count == 1);
				return newNodes[0];
			}
			catch (DbgValueNodeExpansionException ex) {
				var node = new ExpansionErrorValueNode(parent.Language, parent.Runtime, ex.ParentExpression, ex.ErrorMessage);
				parent.Runtime.CloseOnContinue(node);
				return node;
			}
		}

		public override DbgValueNodeInfo Evaluate(string expression) {
			Debug2.Assert(evalInfo is not null);
			return evaluate(evalInfo, expression);
		}
	}
}
