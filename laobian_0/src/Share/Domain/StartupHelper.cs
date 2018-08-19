﻿using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Laobian.Share.Domain.Blog;
using Laobian.Share.Domain.Blog.RandomPost;
using Laobian.Share.Domain.Interface;
using Laobian.Share.Domain.Setting;
using Laobian.Share.Infrastructure.Blob;
using Laobian.Share.Infrastructure.Cache;
using Laobian.Share.Infrastructure.Config;
using Laobian.Share.Infrastructure.Email;
using Laobian.Share.Infrastructure.Interface;
using Laobian.Share.Infrastructure.Repository;
using Laobian.Share.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Laobian.Share.Domain
{
    public class StartupHelper
    {
        public static void ConfigureServices(IServiceCollection services, IConfiguration config)
        {
            services.AddSingleton(HtmlEncoder.Create(allowedRanges: new[] { UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs }));
            services.AddSingleton<IRandomPostGenerator, RandomPostGenerator>();
            services.AddSingleton<IEmailService, EmailService>();
            services.AddSingleton<IBlogPostVisitService, BlogPostVisitService>();
            services.AddSingleton<IBlogService, BlogService>();
            services.AddSingleton<ISettingService, SettingService>();
            services.AddSingleton<ICacheClient, CacheClient>();
            services.AddSingleton<IBlobClient, BlobClient>();
            services.AddSingleton(config);
            services.AddSingleton<ConfigSetting>();
            services.AddSingleton(typeof(IBlobDataRepository<>), typeof(BlobDataRepository<>));

            services.AddDataProtection()
                .SetApplicationName("laobian")
                .PersistKeysToFileSystem(
                    new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        }

        public static void Config(
            IApplicationBuilder app,
            IHostingEnvironment env,
            IApplicationLifetime applicationLifetime,
            DomainName domain)
        {
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });

            var emailService = app.ApplicationServices.GetService<IEmailService>();

            applicationLifetime.ApplicationStarted.Register(async () =>
            {
                await emailService.SendAsync(new EmailMessage
                {
                    EmailDomain = domain,
                    Message = $"Application started at [{DateTime.UtcNow}]"
                });
            });
            applicationLifetime.ApplicationStopped.Register(async () =>
            {
                await emailService.SendAsync(new EmailMessage
                {
                    EmailDomain = domain,
                    Message = $"Application stopped at [{DateTime.UtcNow}]"
                });
            });
            applicationLifetime.ApplicationStopping.Register(async () =>
            {
                await emailService.SendAsync(new EmailMessage
                {
                    EmailDomain = domain,
                    Message = $"Application is stopping at [{DateTime.UtcNow}]"
                });
            });

            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
                app.UseExceptionHandler(new ExceptionHandlerOptions
                {
                    ExceptionHandler = async context =>
                    {
                        var errorFeature = context.Features.Get<IExceptionHandlerFeature>();
                        var message = new EmailMessage
                        {
                            RequestIp = context.Connection.RemoteIpAddress.ToString(),
                            Exception = errorFeature.Error,
                            EmailDomain = domain,
                            Message = "The request throws an exception, please have a check!",
                            RequestHeader = JsonConvert.SerializeObject(context.Request.Headers, Formatting.Indented)
                        };
                        await emailService.SendAsync(message);
                        var settingService = context.RequestServices.GetService<ISettingService>();
                        var email = await settingService.FindAsync<string>(SettingKey.Email);
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync($"Something was wrong! Please contact {email}.");
                    }
                });

            app.UseStatusCodePages(async context =>
            {
                context.HttpContext.Response.ContentType = "text/plain";
                await context.HttpContext.Response.WriteAsync(
                    "Status code page, status code: " +
                    context.HttpContext.Response.StatusCode);
            });
        }
    }
}