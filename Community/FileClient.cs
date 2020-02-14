#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Community.Community
File: FileClient.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Community
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;
	using Ecng.IO;
	using Ecng.Security;

	using MoreLinq;

	using StockSharp.Localization;

	/// <summary>
	/// The client for access to the service of work with files and documents.
	/// </summary>
	public class FileClient : BaseCommunityClient<IFileService>, IFileClient
	{
		private const int _partSize = 100 * 1024; // 100kb

		private readonly CachedSynchronizedDictionary<long, FileData> _cache = new CachedSynchronizedDictionary<long, FileData>(); 

		/// <summary>
		/// Initializes a new instance of the <see cref="FileClient"/>.
		/// </summary>
		public FileClient()
			: this("https://stocksharp.com/services/fileservice.svc".To<Uri>())
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="FileClient"/>.
		/// </summary>
		/// <param name="address">Service address.</param>
		public FileClient(Uri address)
			: base(address, "file")
		{
		}

		/// <summary>
		/// Use compression.
		/// </summary>
		public bool Compression { get; set; } = true;

		/// <summary>
		/// Check hash of downloaded files.
		/// </summary>
		public bool CheckDownloadedHash { get; set; }

		/// <inheritdoc />
		public FileData GetFile(long id, Action<long> progress = null, Func<bool> cancel = null)
		{
			var data = GetFileInfo(id);
			Download(data, progress, cancel);
			return data;
		}

		/// <inheritdoc />
		public FileData GetFileInfo(long id)
		{
			return _cache.SafeAdd(id, key => Invoke(f => f.GetFileInfo(NullableSessionId ?? Guid.Empty, id)));
		}

		/// <inheritdoc />
		public bool Download(FileData data, Action<long> progress = null, Func<bool> cancel = null)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			if (data.Body != null)
				return true;

			var operationId = Invoke(f => f.BeginDownload2(SessionId, data.Id, Compression));

			var body = new List<byte>();

			while (body.Count < data.BodyLength)
			{
				if (cancel?.Invoke() == true)
				{
					Invoke(f => f.FinishDownload2(operationId, true));
					return false;
				}

				var part = Invoke(f => f.ProcessDownload2(operationId, body.Count, _partSize));

				if (Compression)
					part = part.DeflateFrom();

				body.AddRange(part);
				progress?.Invoke(body.Count);
			}

			var hash = Invoke(f => f.FinishDownload2(operationId, false));

			data.Body = body.ToArray();

			if (CheckDownloadedHash)
			{
				var calc = data.Body.Md5();

				if (!hash.CompareIgnoreCase(calc))
					throw new InvalidOperationException(LocalizedStrings.FileHashNotMatchKey.Put(hash, calc));
			}

			return true;
		}

		private static string GetHash(byte[] body)
		{
			if (body == null)
				throw new ArgumentNullException(nameof(body));

			if (body.Length == 0)
				throw new ArgumentOutOfRangeException(nameof(body));

			return body.Md5();
		}

		/// <inheritdoc />
		public void Update(FileData data, Action<long> progress = null, Func<bool> cancel = null)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			if (data.Id == 0)
				throw new ArgumentException(nameof(data));

			var hash = GetHash(data.Body);

			var operationId = Invoke(f => f.BeginUploadExisting2(SessionId, data.Id, Compression, hash));
			Upload(operationId, data, progress, cancel);
		}

		/// <inheritdoc />
		public FileData Upload(string fileName, byte[] body, bool isPublic, Action<long> progress = null, Func<bool> cancel = null)
		{
			if (fileName.IsEmpty())
				throw new ArgumentNullException(nameof(fileName));

			var hash = GetHash(body);

			var operationId = Invoke(f => f.BeginUpload2(SessionId, fileName, isPublic, Compression, hash));

			var data = new FileData
			{
				FileName = fileName,
				Body = body,
				BodyLength = body.LongLength,
				IsPublic = isPublic,
				CreationDate = DateTime.UtcNow,
				Hash = hash,
			};

			var id = Upload(operationId, data, progress, cancel);

			return id == null ? null : data;
		}

		/// <inheritdoc />
		public Guid? UploadTemp(string fileName, byte[] body, Action<long> progress = null, Func<bool> cancel = null)
		{
			if (fileName.IsEmpty())
				throw new ArgumentNullException(nameof(fileName));

			var hash = GetHash(body);

			var operationId = Invoke(f => f.BeginUploadTemp(SessionId, fileName, Compression, hash));

			var data = new FileData
			{
				FileName = fileName,
				Body = body,
				BodyLength = body.LongLength,
				CreationDate = DateTime.UtcNow,
				Hash = hash,
			};

			var id = Upload(operationId, data, progress, cancel);

			if (id == null)
				return null;

			return operationId;
		}

		private long? Upload(Guid operationId, FileData file, Action<long> progress, Func<bool> cancel)
		{
			if (file == null)
				throw new ArgumentNullException(nameof(file));

			var sentCount = 0L;

			var body = file.Body;

			if (Compression)
				body = body.DeflateTo();

			foreach (var part in body.Batch(_partSize))
			{
				if (cancel?.Invoke() == true)
				{
					Invoke(f => f.FinishUpload(operationId, true));
					return null;
				}

				var arr = part.ToArray();

				ValidateError(Invoke(f => f.ProcessUpload(operationId, arr)));

				sentCount += arr.Length;
				progress?.Invoke(sentCount);
			}

			var id = Invoke(f => f.FinishUpload(operationId, false));

			if (id < 0)
				ValidateError((byte)-id);

			// temp upload
			if (id == 0)
				return 0;

			if (file.Id == 0)
				file.Id = id;

			_cache.TryAdd(id, file);

			return id;
		}

		/// <inheritdoc />
		public long GetUploadLimit()
		{
			return Invoke(f => f.GetUploadLimit(SessionId));
		}

		/// <inheritdoc />
		public string Share(long id)
		{
			return Invoke(f => f.Share(SessionId, id));
		}

		/// <inheritdoc />
		public void UnShare(long id)
		{
			Invoke(f => f.UnShare(SessionId, id));
		}

		private static void ValidateError(byte errorCode)
		{
			((ErrorCodes)errorCode).ThrowIfError();
		}
	}
}