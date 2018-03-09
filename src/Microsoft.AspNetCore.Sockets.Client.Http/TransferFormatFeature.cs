// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Sockets.Features;

namespace Microsoft.AspNetCore.Sockets.Client
{
    public class TransferFormatFeature : ITransferFormatFeature
    {
        private TransferFormat _transferFormat;

        public TransferFormat TransferFormat
        {
            get => _transferFormat;
            set
            {
                // Verify the new value
                // The value must be non-zero AND a power of 2 (indicating a single bit is set)
                if(value == 0 || (value & (value - 1)) != 0)
                {
                    throw new ArgumentException($"Cannot set {nameof(TransferFormat)} to a bitwise-OR of values", nameof(TransferFormat));
                }

                _transferFormat = value;
            }
        }
    }
}
