// DI container'a kayıt;
using NEDA.AI.NaturalLanguage;
using NEDA.API.DTOs;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using System;
using System.IO;
using System.Net.Sockets;

services.AddSingleton<IGCPClient, GCPClient>();
services.Configure<GCPConfig>(configuration.GetSection("GoogleCloud"));

// Kullanım;
var gcpClient = serviceProvider.GetRequiredService<IGCPClient>();

// Initialize;
await gcpClient.InitializeAsync();

// Storage işlemleri;
var storageClient = gcpClient.GetStorageClient();
await storageClient.UploadObjectAsync("my-bucket", "file.txt", "text/plain", stream);

// VM listeleme;
var instances = await gcpClient.ListVmInstancesAsync("us-central1-a");

// Secret alma;
var secret = await gcpClient.GetSecretAsync("api-key");

// Pub/Sub mesaj gönderme;
var messageId = await gcpClient.PublishMessageAsync("my-topic", "Hello World");

// Health check;
var health = await gcpClient.CheckHealthAsync();

// Custom API çağrısı;
var response = await gcpClient.CallGcpApiAsync<MyResponse>(
    "compute",
    "GET",
    $"projects/{projectId}/zones/{zone}/instances");
