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

namespace dnSpy.Contracts.Debugger.Evaluation {
	/// <summary>
	/// Thrown by <see cref="DbgValueNode.GetChildren(DbgEvaluationInfo, ulong, int, DbgValueNodeEvaluationOptions)"/>
	/// when an internal debugger error stopped a page of children from being produced.
	///
	/// <para>dgSpy addition. The engine used to answer such a failure with a full page of identical error
	/// nodes, which reads as real members to anything that is not a treeview. Failing the call keeps a
	/// caller able to tell "this object has N broken members" apart from "expansion failed once"; a
	/// consumer that needs a row to draw builds one from <see cref="ErrorMessage"/>.</para>
	/// </summary>
	public sealed class DbgValueNodeExpansionException : Exception {
		/// <summary>
		/// Expression of the node whose children could not be read, or "&lt;expression&gt;" if it has none
		/// </summary>
		public string ParentExpression { get; }

		/// <summary>
		/// Display-ready, already localized error message. This is the text the fabricated error nodes used
		/// to carry, so a UI consumer can show it without further translation.
		/// </summary>
		public string ErrorMessage { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="parentExpression">Expression of the node whose children could not be read</param>
		/// <param name="errorMessage">Display-ready, already localized error message</param>
		/// <param name="innerException">The exception that stopped the expansion, or null</param>
		public DbgValueNodeExpansionException(string parentExpression, string errorMessage, Exception? innerException = null)
			: base(errorMessage, innerException) {
			ParentExpression = parentExpression ?? throw new ArgumentNullException(nameof(parentExpression));
			ErrorMessage = errorMessage ?? throw new ArgumentNullException(nameof(errorMessage));
		}
	}
}
