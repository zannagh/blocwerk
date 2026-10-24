// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client;
using Blocwerk.Client.Store;
using Blocwerk.Client.Views;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddSingleton<IClientStoreSource, JsClientStoreSource>();
builder.Services.AddSingleton<ClientStore>();
builder.Services.AddSingleton<IClientViewEnv, JsClientViewEnv>();
builder.Services.AddSingleton<WallSummaries>();

// The views' extension points (PR 3.11); PR 3.13 mounts gallery and 3D here.
builder.Services.AddSingleton(new ViewSlotRegistry());

await builder.Build().RunAsync();
