using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azconfig.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Azconfig;
using Microsoft.Extensions.DependencyInjection;

namespace ConfigStoreDemo
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            string connection_string = "Endpoint=https://contoso.azconfig.io;Id=xxxxx;Secret=abcdef=";

            // load from local json file and remote config store.
            // load all key-values with null label and listen one key.
            var builder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddRemoteAppConfiguration(connection_string, new RemoteConfigurationOptions().Listen("Settings:BackgroundColor", 1000));

            Configuration = builder.Build();
            AzconfigClient client = new AzconfigClient(connection_string);
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<Settings>(Configuration.GetSection("Settings"));
            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseBrowserLink();
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStaticFiles();

            app.UseMvc();
        }
    }
}
