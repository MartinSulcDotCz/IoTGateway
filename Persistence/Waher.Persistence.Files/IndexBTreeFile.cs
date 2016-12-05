﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Waher.Persistence.Files.Serialization;
using Waher.Persistence.Files.Storage;

namespace Waher.Persistence.Files
{
	/// <summary>
	/// This class manages an index file to a <see cref="ObjectBTreeFile"/>.
	/// </summary>
	public class IndexBTreeFile : IDisposable, IEnumerable<object>
	{
		private ObjectBTreeFile objectFile;
		private ObjectBTreeFile indexFile;
		private IndexRecords recordHandler;

		public IndexBTreeFile(string FileName, int BlocksInCache, ObjectBTreeFile ObjectFile, FilesProvider Provider, params string[] FieldNames)
		{
			this.objectFile = ObjectFile;
			this.recordHandler = new IndexRecords(this.objectFile.CollectionName, this.objectFile.Encoding,
				this.objectFile.InlineObjectSizeLimit, FieldNames);
			this.indexFile = new ObjectBTreeFile(FileName, string.Empty, string.Empty, this.objectFile.BlockSize, BlocksInCache,
				this.objectFile.BlobBlockSize, Provider, this.objectFile.Encoding, this.objectFile.TimeoutMilliseconds,
				this.objectFile.Encrypted, this.recordHandler);
		}

		/// <summary>
		/// <see cref="IDisposable.Dispose"/>
		/// </summary>
		public void Dispose()
		{
			if (this.indexFile != null)
			{
				this.indexFile.Dispose();
				this.indexFile = null;

				this.objectFile = null;
				this.recordHandler = null;
			}
		}

		/// <summary>
		/// Object file.
		/// </summary>
		public ObjectBTreeFile ObjectFile
		{
			get { return this.objectFile; }
		}

		/// <summary>
		/// Index file.
		/// </summary>
		public ObjectBTreeFile IndexFile
		{
			get { return this.indexFile; }
		}

		/// <summary>
		/// Saves a new object to the file.
		/// </summary>
		/// <param name="ObjectId">Object ID</param>
		/// <param name="Object">Object to persist.</param>
		/// <param name="Serializer">Object serializer.</param>
		/// <returns>If the object was saved in the index (true), or if the index property values of the object did not exist, or were too big to fit in an index record.</returns>
		internal async Task<bool> SaveNewObject(Guid ObjectId, object Object, IObjectSerializer Serializer)
		{
			byte[] Bin = this.recordHandler.Serialize(ObjectId, Object, Serializer);
			if (Bin.Length > this.indexFile.InlineObjectSizeLimit)
				return false;

			await this.indexFile.Lock();
			try
			{
				BlockInfo Leaf = await this.indexFile.FindLeafNodeLocked(Bin);
				await this.indexFile.InsertObjectLocked(Leaf.BlockIndex, Leaf.Header, Leaf.Block, Bin, Leaf.InternalPosition, 0, 0, true, Leaf.LastObject);
			}
			finally
			{
				await this.indexFile.Release();
			}

			return true;
		}

		/// <summary>
		/// Deletes an object from the file.
		/// </summary>
		/// <param name="ObjectId">Object ID</param>
		/// <param name="Object">Object to delete.</param>
		/// <param name="Serializer">Object serializer.</param>
		/// <returns>If the object was saved in the index (true), or if the index property values of the object did not exist, or were too big to fit in an index record.</returns>
		internal async Task<bool> DeleteObject(Guid ObjectId, object Object, IObjectSerializer Serializer)
		{
			byte[] Bin = this.recordHandler.Serialize(ObjectId, Object, Serializer);
			if (Bin.Length > this.indexFile.InlineObjectSizeLimit)
				return false;

			await this.indexFile.Lock();
			try
			{
				await this.indexFile.DeleteObjectLocked(Bin, false, true, Serializer, null);
			}
			finally
			{
				await this.indexFile.Release();
			}

			return true;
		}

		/// <summary>
		/// Updates an object in the file.
		/// </summary>
		/// <param name="ObjectId">Object ID</param>
		/// <param name="OldObject">Object that is being changed.</param>
		/// <param name="NewObject">New version of object.</param>
		/// <param name="Serializer">Object serializer.</param>
		/// <returns>If the object was saved in the index (true), or if the index property values of the object did not exist, or were too big to fit in an index record.</returns>
		internal async Task<bool> UpdateObject(Guid ObjectId, object OldObject, object NewObject, IObjectSerializer Serializer)
		{
			byte[] OldBin = this.recordHandler.Serialize(ObjectId, OldObject, Serializer);
			if (OldBin.Length > this.indexFile.InlineObjectSizeLimit)
				return false;

			byte[] NewBin = this.recordHandler.Serialize(ObjectId, NewObject, Serializer);
			if (NewBin.Length > this.indexFile.InlineObjectSizeLimit)
				return false;

			await this.indexFile.Lock();
			try
			{
				await this.indexFile.DeleteObjectLocked(OldBin, false, true, Serializer, null);

				BlockInfo Leaf = await this.indexFile.FindLeafNodeLocked(NewBin);
				await this.indexFile.InsertObjectLocked(Leaf.BlockIndex, Leaf.Header, Leaf.Block, NewBin, Leaf.InternalPosition, 0, 0, true, Leaf.LastObject);
			}
			finally
			{
				await this.indexFile.Release();
			}

			return true;
		}

		/// <summary>
		/// Clears the database of all objects.
		/// </summary>
		/// <returns>Task object.</returns>
		internal Task ClearAsync()
		{
			return this.indexFile.ClearAsync();
		}

		/// <summary>
		/// Returns an untyped enumerator that iterates through the collection in the order specified by the index.
		/// 
		/// For a typed enumerator, call the <see cref="GetTypedEnumerator{T}(bool)"/> method.
		/// </summary>
		/// <returns>An enumerator that can be used to iterate through the collection.</returns>
		public IEnumerator<object> GetEnumerator()
		{
			return new IndexBTreeFileEnumerator<object>(this, false, this.recordHandler);
		}

		/// <summary>
		/// Returns an untyped enumerator that iterates through the collection in the order specified by the index.
		/// 
		/// For a typed enumerator, call the <see cref="GetTypedEnumerator{T}(bool)"/> method.
		/// </summary>
		/// <returns>An enumerator that can be used to iterate through the collection.</returns>
		IEnumerator IEnumerable.GetEnumerator()
		{
			return new IndexBTreeFileEnumerator<object>(this, false, this.recordHandler);
		}

		/// <summary>
		/// Returns an typed enumerator that iterates through the collection in the order specified by the index. The typed enumerator uses
		/// the object serializer of <typeparamref name="T"/> to deserialize objects by default.
		/// </summary>
		/// <param name="Locked">If locked access to the file is requested.
		/// 
		/// If unlocked access is desired, any change to the database will invalidate the enumerator, and further access to the
		/// enumerator will cause an <see cref="InvalidOperationException"/> to be thrown.
		/// 
		/// If locked access is desired, the database cannot be updated, until the enumerator has been dispose. Make sure to call
		/// the <see cref="ObjectBTreeFileEnumerator{T}.Dispose"/> method when done with the enumerator, to release the database
		/// after use.</param>
		/// <returns>An enumerator that can be used to iterate through the collection.</returns>
		public IndexBTreeFileEnumerator<T> GetTypedEnumerator<T>(bool Locked)
		{
			return new IndexBTreeFileEnumerator<T>(this, false, this.recordHandler);
		}

		/// <summary>
		/// Calculates the rank of an object in the database, given its Object ID.
		/// </summary>
		/// <param name="ObjectId">Object ID</param>
		/// <returns>Rank of object in database.</returns>
		/// <exception cref="IOException">If the object is not found.</exception>
		public async Task<ulong> GetRank(Guid ObjectId)
		{
			object Object = await this.objectFile.LoadObject(ObjectId);

			Type ObjectType = Object.GetType();
			IObjectSerializer Serializer = this.objectFile.Provider.GetObjectSerializer(ObjectType);

			byte[] Key = this.recordHandler.Serialize(ObjectId, Object, Serializer);

			await this.indexFile.Lock();
			try
			{
				return await this.indexFile.GetRankLocked(Key);
			}
			finally
			{
				await this.indexFile.Release();
			}
		}

		/// <summary>
		/// Regenerates the index.
		/// </summary>
		/// <returns></returns>
		public async Task Regenerate()
		{
			await this.ClearAsync();

			using (ObjectBTreeFileEnumerator<object> e = this.objectFile.GetTypedEnumerator<object>(true))
			{
				while (e.MoveNext())
					await this.SaveNewObject((Guid)e.CurrentObjectId, e.Current, e.CurrentSerializer);
			}
		}

	}
}