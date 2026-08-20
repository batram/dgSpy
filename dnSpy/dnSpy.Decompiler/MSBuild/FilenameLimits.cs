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

namespace dnSpy.Decompiler.MSBuild {
	/// <summary>
	/// Max lengths used when generating project, file and directory names. The defaults keep
	/// generated paths well inside the classic Windows MAX_PATH limit, at the cost of truncating
	/// long assembly and namespace names. Use 0 (or a negative value) to disable a limit.
	/// </summary>
	sealed class FilenameLimits {
		/// <summary>
		/// Default limits (<see cref="MaxNameLength"/> = 60, <see cref="MaxDirNameLength"/> = 40)
		/// </summary>
		public static readonly FilenameLimits Default = new FilenameLimits();

		/// <summary>
		/// Max length of a filename, excluding extension. This is also the max length of a
		/// generated project directory name. 0 or less means no limit.
		/// </summary>
		public int MaxNameLength { get; set; } = 60;

		/// <summary>
		/// Max length of a directory name part. 0 or less means no limit.
		/// </summary>
		public int MaxDirNameLength { get; set; } = 40;
	}
}
