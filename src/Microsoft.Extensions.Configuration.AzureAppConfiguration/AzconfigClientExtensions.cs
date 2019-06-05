﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.AppConfiguration.Azconfig;

namespace Microsoft.Extensions.Configuration.AzureAppConfiguration
{
    internal static class AzconfigClientExtensions
    {
        public static IObservable<KeyValueChange> Observe(this AzconfigClient client, IKeyValue keyValue, TimeSpan pollInterval, IScheduler scheduler = null, bool requestTracingEnabled = true)
        {
            scheduler = scheduler ?? Scheduler.Default;
            RequestOptionsBase options = null;
            if (requestTracingEnabled)
            {
                options = new RequestOptionsBase();
                options.AddRequestType(RequestType.Watch);
            }

            return Observable
                .Timer(pollInterval, scheduler)
                .SelectMany(_ => Observable
                    .FromAsync((cancellationToken) => {
                        return SafeInvoke<Task<IKeyValue>>(
                            client,
                            "GetCurrentKeyValue",
                            new object[] { keyValue, options, cancellationToken },
                            client.RetryOptions.MaxRetries,
                            client.RetryOptions.MaxRetryWaitTime);
                    })
                    .Delay(pollInterval, scheduler)
                    .Repeat()
                    .Where(kv => kv != keyValue)
                    .Select(kv =>
                    {
                        keyValue = kv ?? new KeyValue(keyValue.Key)
                        {
                            Label = keyValue.Label
                        };

                        return new KeyValueChange()
                        {
                            ChangeType = kv == null ? KeyValueChangeType.Deleted : KeyValueChangeType.Modified,
                            Current = kv,
                            Key = keyValue.Key,
                            Label = keyValue.Label
                        };
                    }));
        }

        public static IObservable<IEnumerable<KeyValueChange>> ObserveKeyValueCollection(this AzconfigClient client, ObserveKeyValueCollectionOptions options, IEnumerable<IKeyValue> keyValues, bool requestTracingEnabled = true)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (keyValues == null)
            {
                keyValues = Enumerable.Empty<IKeyValue>();
            }

            if (options.Prefix == null)
            {
                options.Prefix = string.Empty;
            }

            if (options.Prefix.Contains("*"))
            {
                throw new ArgumentException("The prefix cannot contain '*'", $"{nameof(options)}.{nameof(options.Prefix)}");
            }

            if (keyValues.Any(k => string.IsNullOrEmpty(k.Key)))
            {
                throw new ArgumentNullException($"{nameof(keyValues)}[].{nameof(IKeyValue.Key)}");
            }

            if (string.IsNullOrEmpty(options.Prefix) && keyValues.Any(k => !k.Key.StartsWith(options.Prefix)))
            {
                throw new ArgumentException("All observed key-values must start with the provided prefix.", $"{nameof(keyValues)}[].{nameof(IKeyValue.Key)}");
            }

            if (keyValues.Any(k => !string.Equals(NormalizeNull(k.Label), NormalizeNull(options.Label))))
            {
                throw new ArgumentException("All observed key-values must use the same label.", $"{nameof(keyValues)}[].{nameof(IKeyValue.Key)}");
            }

            if (keyValues.Any(k => k.Label != null && k.Label.Contains("*")))
            {
                throw new ArgumentException("The label filter cannot contain '*'", $"{nameof(options)}.{nameof(options.Label)}");
            }

            var scheduler = Scheduler.Default;
            Dictionary<string, string> currentEtags = keyValues.ToDictionary(kv => kv.Key, kv => kv.ETag);
            var queryOptions = new QueryKeyValueCollectionOptions()
            {
                KeyFilter = options.Prefix + "*",
                LabelFilter = string.IsNullOrEmpty(options.Label) ? LabelFilter.Null : options.Label,
                FieldsSelector = KeyValueFields.ETag | KeyValueFields.Key
            };

            if (requestTracingEnabled)
            {
                queryOptions.AddRequestType(RequestType.Watch);
            }

            return Observable
                .Timer(options.PollInterval, scheduler)
                .SelectMany(_ => Observable
                    .FromAsync((cancellationToken) =>
                        SafeInvoke<IAsyncEnumerable<IKeyValue>>(
                            client,
                            "GetKeyValues",
                            new object[] { queryOptions },
                            client.RetryOptions.MaxRetries,
                            client.RetryOptions.MaxRetryWaitTime)
                        .ToEnumerableAsync(cancellationToken))
                        .Delay(options.PollInterval, scheduler)
                        .Repeat()
                        .Where(kvs =>
                        {
                            bool changed = false;
                            var etags = currentEtags.ToDictionary(kv => kv.Key, kv => kv.Value);
                            foreach (IKeyValue kv in kvs)
                            {
                                if (!etags.TryGetValue(kv.Key, out string etag) || !etag.Equals(kv.ETag))
                                {
                                    changed = true;
                                    break;
                                }

                                etags.Remove(kv.Key);
                            }

                            if (!changed && etags.Any())
                            {
                                changed = true;
                            }

                            return changed;
                        }))
                        .SelectMany(_ => Observable.FromAsync(async cancellationToken =>
                        {
                            queryOptions = new QueryKeyValueCollectionOptions()
                            {
                                KeyFilter = options.Prefix + "*",
                                LabelFilter = string.IsNullOrEmpty(options.Label) ? LabelFilters.Null : options.Label
                            };

                            if (requestTracingEnabled)
                            {
                                queryOptions.AddRequestType(RequestType.Watch);
                            }

                            IEnumerable<IKeyValue> kvs = await client.GetKeyValues(queryOptions).ToEnumerableAsync(cancellationToken);

                            var etags = currentEtags.ToDictionary(kv => kv.Key, kv => kv.Value);
                            currentEtags = kvs.ToDictionary(kv => kv.Key, kv => kv.ETag);
                            var changes = new List<KeyValueChange>();

                            foreach (IKeyValue kv in kvs)
                            {
                                if (!etags.TryGetValue(kv.Key, out string etag) || !etag.Equals(kv.ETag))
                                {
                                    changes.Add(new KeyValueChange()
                                    {
                                        ChangeType = KeyValueChangeType.Modified,
                                        Key = kv.Key,
                                        Label = NormalizeNull(options.Label),
                                        Current = kv
                                    });
                                }

                                etags.Remove(kv.Key);
                            }

                            foreach (var kvp in etags)
                            {
                                changes.Add(new KeyValueChange()
                                {
                                    ChangeType = KeyValueChangeType.Deleted,
                                    Key = kvp.Key,
                                    Label = NormalizeNull(options.Label),
                                    Current = null
                                });
                            }

                            return changes;
                        }));
        }

        private static string NormalizeNull(string s)
        {
            if (s == null || s == LabelFilter.Null)
            {
                return null;
            }

            return s;
        }

        private static T SafeInvoke<T>(Object obj, string methodName, object[] args, int maxRetries, TimeSpan retryAfter)
        {
            int attempts = 0;
            while (true)
            {
                try
                {
                    Type type = obj.GetType();
                    MethodInfo method = type.GetMethod(methodName);
                    return (T)method.Invoke(obj, args);
                }
                catch (Exception ex)
                {
                    if (!IsRetriableException(ex))
                    {
                        throw;
                    }
                    else if (++attempts <= maxRetries)
                    {
                        Thread.Sleep(retryAfter);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            //
            // For retriable exceptions with no more retries left, skip retry and ignore the exception.
            return default(T);
        }

        private static bool IsRetriableException(Exception ex)
        {
            //
            // List of retriable exceptions.
            if (ex is ArgumentException
                || ex is HttpRequestException
                || ex is SocketException
                || ex is TimeoutException
                || ex is AzconfigException)
            {
                return true;
            }

            //
            // If aggregate exception, check list of inner exceptions.
            if (ex is AggregateException aggregateEx)
            {
                return aggregateEx.InnerExceptions.Any(innerEx => IsRetriableException(innerEx));
            }

            return false;
        }
    }
}
