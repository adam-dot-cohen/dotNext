﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DotNext.Threading
{
    /// <summary>
    /// Provides concurrent object pool where object selection is thread-safe except the rented object.
    /// </summary>
    /// <typeparam name="T">Type of objects in the pool.</typeparam>
    public class ConcurrentObjectPool<T> : Disposable
        where T : class
    {
        /// <summary>
        /// Represents rented object.
        /// </summary>
        /// <remarks>
        /// Call <see cref="IDisposable.Dispose"/> to return object back to the pool.
        /// </remarks>
        public interface IRental : IDisposable
        {
            /// <summary>
            /// Gets rented object.
            /// </summary>
            T Resource { get; }
        }

        /*
         * Actual rental object is a node in double linked ring buffer. 
         * 
         */
        private sealed class Rental : IRental
        {
            //cached delegate to avoid memory allocations and increase chance of inline caching
            private static readonly WaitCallback DisposeResource = resource => (resource as IDisposable)?.Dispose();
            private AtomicBoolean lockState;
            private T resource; //this is not volatile because it's consistency is protected by lockState memory barrier
            private readonly int rank;
            private readonly long maxWeight;
            private long weight;

            internal Rental(int rank, long weight)
            {
                this.rank = rank;
                this.weight = maxWeight = weight;
            }

            internal Rental(int rank, T resource)
            {
                this.rank = rank;
                this.resource = resource;
            }

            internal bool IsFirst => rank == 0;

            internal Rental Next
            {
                get;
                private set;
            }

            internal Rental Previous
            {
                get;
                private set;
            }

            //indicates that this object is a predecessor of the specified object in the ring buffer
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool IsPredecessorOf(Rental other) => rank < other.rank;

            internal void Attach(Rental next)
            {
                Next = next;
                next.Previous = this;
            }

            internal event Action<Rental> Released;

            T IRental.Resource => resource;

            //instantiate resource if needed
            //call to this method must be protected by the lock using TryAcquire
            internal void CreateResourceIfNeeded(Func<T> factory)
            {
                if(resource is null)
                    resource = factory();
            }

            internal bool TryAcquire() => lockState.FalseToTrue();

            //this method indicates that the object is requested
            //and no longer starving
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Renew() => weight.VolatileWrite(maxWeight);

            //used by SJF strategy only
            internal bool Starve()
            {
                bool success;
                if (success = lockState.FalseToTrue())  //acquire lock
                {
                    if(success = weight.DecrementAndGet() <= 0) //decrease weight because this object was accessed a long time ago
                    {
                        //prevent this method from blocking so dispose resource asynchronously
                        if(resource is IDisposable)
                            ThreadPool.QueueUserWorkItem(DisposeResource, resource);
                        resource = null;
                    }
                    lockState.Value = false;
                }
                return success;
            }

            void IDisposable.Dispose()
            {
                lockState.Value = false;    //release the lock
                Released?.Invoke(this);     //notify that this object is returned back to the pool
            }

            internal void Destroy(bool disposeResource)
            {
                if(!(Next is null))
                {
                    Next.Previous = null;
                    Next = null;
                }
                if(!(Previous is null))
                {
                    Previous.Next = null;
                    Previous = null;
                }
                Released = null;
                if (disposeResource && resource is IDisposable disposable)
                    disposable.Dispose();
                resource = null;
            }
        }

        //cached delegates to avoid allocations
        private static readonly Func<Rental, Rental, Rental> SelectLastRenal = (current, update) => current is null || current.IsPredecessorOf(update) ? update : current;
        private static readonly Func<Rental, Rental> SelectNextRental = current => current.Next;
        private readonly Func<T> factory;
        private AtomicReference<Rental> last, current;
        private int waitCount;

        public ConcurrentObjectPool(int capacity, Func<T> factory)
        {
            if(capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            this.factory = factory;
            var rental = default(Rental);
            Action<Rental> callback = AdjustAvailableObjectAndCheckStarvation;
            for(var index = 0; index < capacity; index++)
            {
                var next = new Rental(index, capacity + capacity / 2L);
                next.Released += callback;  
                if(rental is null)
                    current = last = new AtomicReference<Rental>(rental = next);
                else
                {
                    rental.Attach(next);
                    rental = next;
                }
            }
            rental.Attach(current.Value);
            Capacity = capacity;
        }

        public ConcurrentObjectPool(IEnumerable<T> objects)
        {
            this.factory = null;
            var rental = default(Rental);
            var index = 0;
            foreach(var resource in objects)
            {
                var next = new Rental(index++, resource);
                if(rental is null)
                    current = last = new AtomicReference<Rental>(rental = next);
                else
                {
                    rental.Attach(next);
                    rental = next;
                }
            }
            if(index == 0)
                throw new ArgumentException(ExceptionMessages.CollectionIsEmpty, nameof(objects));
            rental.Attach(current.Value);
            Capacity = index;
        }

        //release object according with Shortest Job First algorithm
        private void AdjustAvailableObjectAndCheckStarvation(Rental rental)
        {
            current.Value = rental;
            rental = last.AccumulateAndGet(rental, SelectLastRenal);
            //starvation detected, dispose the resource stored in rental object
            if (rental.Starve())
                last.Value = rental.IsFirst ? null : rental.Previous;
        }

        /// <summary>
        /// Gets total count of objects in this pool.
        /// </summary>
        public int Capacity { get; }

        /// <summary>
        /// Gets number of threads waiting for the first available object.
        /// </summary>
        /// <remarks>
        /// This property is for diagnostics purposes.
        /// Ideally, it should be always 0. But in reality, some threads
        /// may wait for the first released object a very small amount of time.
        /// Therefore, the expected value should not be greater than <see cref="Capacity"/> divided by 2,
        /// and do not grow over time. Otherwise, you should increase the capacity.
        /// </remarks>
        public int WaitCount => waitCount;

        /// <summary>
        /// Rents the object from this pool.
        /// </summary>
        /// <returns>The object allows to control lifetime of the rent.</returns>
        public IRental Rent()
        {
            waitCount.IncrementAndGet();
            for (var spinner = new SpinWait(); ; spinner.SpinOnce())
            {
                var rental = current.UpdateAndGet(SelectNextRental);
                if (rental.TryAcquire())
                {
                    waitCount.DecrementAndGet();
                    rental.Renew();
                    if (!(factory is null))
                        rental.CreateResourceIfNeeded(factory);
                    return rental;
                }
            }
        }

        /// <summary>
        /// Releases all resources associated with this object pool.
        /// </summary>
        /// <remarks>
        /// This method is not thread-safe and may not be used concurrently with other members of this instance.
        /// Additionally, this method disposes all objects stored in the pool if it was created
        /// with <see cref="ConcurrentObjectPool(int, Func{T})"/> constructor.
        /// </remarks>
        /// <param name="disposing"><see langword="true"/> if called from <see cref="Disposable.Dispose()"/>; <see langword="false"/> if called from finalizer <see cref="Disposable.Finalize()"/>.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                for(Rental rental = current.Value, next = null; !(rental is null); rental = next)
                {
                    next = rental.Next;
                    rental.Destroy(!(factory is null));
                }
                current = last = default;
            }
            base.Dispose(disposing);
        }
    }
}
