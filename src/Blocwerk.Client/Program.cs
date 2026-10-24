// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client;
using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddSingleton<IClientStoreSource, JsClientStoreSource>();
builder.Services.AddSingleton<ClientStore>();
builder.Services.AddSingleton<ISyncTransport, JsSyncTransport>();
builder.Services.AddSingleton<ISyncClock, SyncClock>();
builder.Services.AddSingleton<ClientSync>();

await builder.Build().RunAsync();
