﻿#nullable enable
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading;
    using System.Threading.Tasks;

    namespace CollapseLauncher.Helper.Background.Loaders
{
    [SuppressMessage("ReSharper", "IdentifierTypo")]
    [SuppressMessage("ReSharper", "PossibleNullReferenceException")]
    [SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
    internal interface IBackgroundMediaLoader : IDisposable
    {
        bool IsBackgroundDimm { get; set; }

        ValueTask LoadAsync(string filePath, bool isForceRecreateCache = false, bool isRequestInit = false, CancellationToken token = default);
        void Dimm(CancellationToken   token = default);
        void Undimm(CancellationToken token = default);
        ValueTask ShowAsync(CancellationToken   token = default);
        ValueTask HideAsync(CancellationToken   token = default);
        void Mute();
        void Unmute();
        void SetVolume(double value);
        void WindowFocused();
        void WindowUnfocused();
        void Play();
        void Pause();
    }
}
