﻿namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Microsoft.Threading;

	public abstract class AsyncReaderWriterResourceLock<TMoniker, TResource>
		where TResource : class {
		private readonly AsyncReaderWriterLock asyncLock = new AsyncReaderWriterLock();

		private readonly Helper helper;

		public AsyncReaderWriterResourceLock() {
			this.helper = new Helper(this);
		}

		/// <summary>
		/// Flags that modify default lock behavior.
		/// </summary>
		[Flags]
		public enum LockFlags {
			/// <summary>
			/// The default behavior applies.
			/// </summary>
			None = 0x0,

			/// <summary>
			/// Causes an upgradeable reader to remain in an upgraded-write state once upgraded,
			/// even after the nested write lock has been released.
			/// </summary>
			/// <remarks>
			/// This is useful when you have a batch of possible write operations to apply, which
			/// may or may not actually apply in the end, but if any of them change anything,
			/// all of their changes should be seen atomically (within a single write lock).
			/// This approach is preferable to simply acquiring a write lock around the batch of
			/// potential changes because it doesn't defeat concurrent readers until it knows there
			/// is a change to actually make.
			/// </remarks>
			StickyWrite = 0x1,

			/// <summary>
			/// Skips a step to make sure that a project is initially evaluated when retrieved using <see cref="IDirectAccess.GetProject"/>.
			/// Setting this flag can have negative side effects to components that write to the MSBuild project,
			/// so use to improve performance of bulk operations where you know re-evaluating the project
			/// is not necessary to maintain a consistent state.
			/// </summary>
			/// <remarks>
			/// This flag is dormant for non-write locks.  But if present on an upgradeable read lock,
			/// this flag will activate for a nested write lock.
			/// </remarks>
			SkipInitialPreparation = 0x1000,
		}

		public bool IsReadLockHeld {
			get { return this.asyncLock.IsReadLockHeld; }
		}

		public bool IsUpgradeableReadLockHeld {
			get { return this.asyncLock.IsUpgradeableReadLockHeld; }
		}

		public bool IsWriteLockHeld {
			get { return this.asyncLock.IsWriteLockHeld; }
		}

		protected bool IsAnyLockHeld {
			get { return this.asyncLock.IsReadLockHeld || this.asyncLock.IsUpgradeableReadLockHeld || this.asyncLock.IsWriteLockHeld; }
		}

		public void OnBeforeWriteLockReleased(Func<Task> action) {
			this.asyncLock.OnBeforeWriteLockReleased(action);
		}

		public ResourceReleaser ReadLock(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(this.asyncLock.ReadLock(cancellationToken), this.helper);
		}

		public ResourceAwaitable ReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(this.asyncLock.ReadLockAsync(cancellationToken), this.helper);
		}

		public ResourceReleaser UpgradeableReadLock(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(this.asyncLock.UpgradeableReadLock((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		public ResourceReleaser UpgradeableReadLock(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(this.asyncLock.UpgradeableReadLock(cancellationToken), this.helper);
		}

		public ResourceAwaitable UpgradeableReadLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(this.asyncLock.UpgradeableReadLockAsync((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		public ResourceAwaitable UpgradeableReadLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(this.asyncLock.UpgradeableReadLockAsync(cancellationToken), this.helper);
		}

		public ResourceReleaser WriteLock(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(this.asyncLock.WriteLock(cancellationToken), this.helper);
		}

		public ResourceReleaser WriteLock(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceReleaser(this.asyncLock.WriteLock((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		public ResourceAwaitable WriteLockAsync(CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(this.asyncLock.WriteLockAsync(cancellationToken), this.helper);
		}

		public ResourceAwaitable WriteLockAsync(LockFlags options, CancellationToken cancellationToken = default(CancellationToken)) {
			return new ResourceAwaitable(this.asyncLock.WriteLockAsync((AsyncReaderWriterLock.LockFlags)options, cancellationToken), this.helper);
		}

		public ResourceSuppression HideLocks() {
			return new ResourceSuppression(this.asyncLock.HideLocks());
		}

		protected abstract Task<TResource> GetResourceAsync(TMoniker projectMoniker);

		protected abstract Task<TResource> PrepareResourceForConcurrentAccessAsync(TResource resource);

		protected abstract Task<TResource> PrepareResourceForExclusiveAccessAsync(TResource resource);

		internal class Helper {
			private readonly AsyncReaderWriterResourceLock<TMoniker, TResource> service;

			private readonly Func<object, Task<TResource>> prepareResourceConcurrentDelegate;

			private readonly Func<object, Task<TResource>> prepareResourceExclusiveDelegate;

			private readonly Func<Task<TResource>, object, Task<TResource>> prepareResourceConcurrentContinuationDelegate;

			private readonly Func<Task<TResource>, object, Task<TResource>> prepareResourceExclusiveContinuationDelegate;

			private readonly object syncObject = new object();

			/// <summary>
			/// A map of projects to the tasks that most recently began evaluating them.
			/// </summary>
			private readonly ConditionalWeakTable<TResource, Task<TResource>> projectEvaluationTasks = new ConditionalWeakTable<TResource, Task<TResource>>();

			internal Helper(AsyncReaderWriterResourceLock<TMoniker, TResource> service) {
				this.service = service;
				this.prepareResourceConcurrentDelegate = state => this.service.PrepareResourceForConcurrentAccessAsync((TResource)state);
				this.prepareResourceExclusiveDelegate = state => this.service.PrepareResourceForExclusiveAccessAsync((TResource)state);
				this.prepareResourceConcurrentContinuationDelegate = (prev, state) => this.service.PrepareResourceForConcurrentAccessAsync((TResource)state);
				this.prepareResourceExclusiveContinuationDelegate = (prev, state) => this.service.PrepareResourceForExclusiveAccessAsync((TResource)state);
			}

			public async Task<TResource> GetResourceAsync(TMoniker resourceMoniker, CancellationToken cancellationToken) {
				using (var projectLock = this.AcquirePreexistingLockOrThrow()) {
					var resource = await this.service.GetResourceAsync(resourceMoniker);
					Task<TResource> preparationTask;

					lock (this.syncObject) {
						if (this.service.asyncLock.LockStackContains((AsyncReaderWriterLock.LockFlags)LockFlags.SkipInitialPreparation)) {
							return resource;
						} else {
							// We can't currently use the caller's cancellation token for this task because 
							// this task may be shared with others or call this method later, and we wouldn't 
							// want their requests to be cancelled as a result of this first caller cancelling.
							if (!this.projectEvaluationTasks.TryGetValue(resource, out preparationTask)) {
								var preparationDelegate = this.service.IsWriteLockHeld ? prepareResourceExclusiveDelegate : prepareResourceConcurrentDelegate;
								preparationTask = Task.Factory.StartNew(preparationDelegate, resource, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default).Unwrap();
							} else {
								var preparationDelegate = this.service.IsWriteLockHeld ? prepareResourceExclusiveContinuationDelegate : prepareResourceConcurrentContinuationDelegate;
								preparationTask = preparationTask.ContinueWith(preparationDelegate, resource, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
								this.projectEvaluationTasks.Remove(resource);
							}
						}

						this.projectEvaluationTasks.Add(resource, preparationTask);
					}

					var result = await preparationTask;
					return result;
				}
			}

			public void OnDispose(ResourceReleaser releaser) {
				releaser.LockReleaser.Dispose();
			}

			private ResourceReleaser AcquirePreexistingLockOrThrow() {
				Verify.Operation(this.service.IsAnyLockHeld, "A lock is required");
				return this.service.ReadLock(CancellationToken.None);
			}
		}

		public struct ResourceAwaitable {
			private readonly AsyncReaderWriterLock.Awaitable awaitable;

			private readonly Helper helper;

			internal ResourceAwaitable(AsyncReaderWriterLock.Awaitable awaitable, Helper helper) {
				this.awaitable = awaitable;
				this.helper = helper;
			}

			public ResourceAwaiter GetAwaiter() {
				return new ResourceAwaiter(this.awaitable.GetAwaiter(), this.helper);
			}
		}

		public struct ResourceAwaiter : INotifyCompletion {
			private readonly AsyncReaderWriterLock.Awaiter awaiter;

			private readonly Helper helper;

			internal ResourceAwaiter(AsyncReaderWriterLock.Awaiter awaiter, Helper helper) {
				this.awaiter = awaiter;
				this.helper = helper;
			}

			/// <summary>
			/// Gets a value indicating whether the lock has been issued.
			/// </summary>
			public bool IsCompleted {
				get { return this.awaiter.IsCompleted; }
			}

			/// <summary>
			/// Sets the delegate to execute when the lock is available.
			/// </summary>
			/// <param name="continuation">The delegate.</param>
			public void OnCompleted(Action continuation) {
				this.awaiter.OnCompleted(continuation);
			}

			/// <summary>
			/// Applies the issued lock to the caller and returns the value used to release the lock.
			/// </summary>
			/// <returns>The value to dispose of to release the lock.</returns>
			public ResourceReleaser GetResult() {
				return new ResourceReleaser(this.awaiter.GetResult(), helper);
			}
		}

		public struct ResourceReleaser : IDisposable {
			private readonly AsyncReaderWriterLock.Releaser releaser;

			private readonly Helper helper;

			internal ResourceReleaser(AsyncReaderWriterLock.Releaser releaser, Helper helper) {
				this.releaser = releaser;
				this.helper = helper;
			}

			internal AsyncReaderWriterLock.Releaser LockReleaser {
				get { return this.releaser; }
			}

			public Task<TResource> GetResourceAsync(TMoniker resourceMoniker, CancellationToken cancellationToken = default(CancellationToken)) {
				return this.helper.GetResourceAsync(resourceMoniker, cancellationToken);
			}

			public void Dispose() {
				this.helper.OnDispose(this);
			}
		}

		public struct ResourceSuppression : IDisposable {
			private readonly AsyncReaderWriterLock.Suppression suppression;

			internal ResourceSuppression(AsyncReaderWriterLock.Suppression suppression) {
				this.suppression = suppression;
			}

			public void Dispose() {
				this.suppression.Dispose();
			}
		}
	}
}
