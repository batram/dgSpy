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

using System.Globalization;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;

namespace dnSpy.Debugger.Evaluation.ViewModel.Impl {
	/// <summary>
	/// dgSpy addition. The row the variables windows draw when expanding a node threw
	/// <see cref="DbgValueNodeExpansionException"/>.
	///
	/// The engine used to fabricate these itself, one per requested child, which is why it is the same
	/// "&lt;error&gt;" name, the same message and the same error image as before — this is the GUI half of
	/// that behaviour, moved to the only place that wants it. It has no engine resources to release, so
	/// closing it is a no-op; it is still registered with the runtime so it dies with the rest of the
	/// snapshot on continue and the DEBUG <see cref="DbgObject"/> finalizer assert stays quiet.
	/// </summary>
	sealed class ExpansionErrorValueNode : DbgValueNode {
		public override DbgLanguage Language { get; }
		public override DbgRuntime Runtime { get; }
		public override string? ErrorMessage { get; }
		public override DbgValue? Value => null;
		public override bool CanEvaluateExpression => false;
		public override string Expression { get; }
		public override string ImageName => PredefinedDbgValueNodeImageNames.Error;
		public override bool IsReadOnly => true;
		public override bool CausesSideEffects => false;
		public override bool? HasChildren => false;

		public ExpansionErrorValueNode(DbgLanguage language, DbgRuntime runtime, string expression, string errorMessage) {
			Language = language;
			Runtime = runtime;
			Expression = expression;
			ErrorMessage = errorMessage;
		}

		public override ulong GetChildCount(DbgEvaluationInfo evalInfo) => 0;

		public override DbgValueNode[] GetChildren(DbgEvaluationInfo evalInfo, ulong index, int count, DbgValueNodeEvaluationOptions options) =>
			System.Array.Empty<DbgValueNode>();

		public override void Format(DbgEvaluationInfo evalInfo, IDbgValueNodeFormatParameters options, CultureInfo? cultureInfo) {
			options.NameOutput?.Write(DbgTextColor.Error, "<error>");
			options.ValueOutput?.Write(DbgTextColor.Error, ErrorMessage!);
		}

		public override DbgValueNodeAssignmentResult Assign(DbgEvaluationInfo evalInfo, string expression, DbgEvaluationOptions options) =>
			new DbgValueNodeAssignmentResult(DbgEEAssignmentResultFlags.None, ErrorMessage);

		protected override void CloseCore(DbgDispatcher dispatcher) { }
	}
}
