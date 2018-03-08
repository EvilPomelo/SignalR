// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

import baseConfig from "../rollup-base"

export default baseConfig(__dirname, {
    ["@aspnet/signalr"]: "signalR",
    msgpack5: "msgpack5"
});