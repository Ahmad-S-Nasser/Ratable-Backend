# Hosting on IIS

The API runs in-process under IIS with the ASP.NET Core Module. No Docker in production.

1. Install the .NET 10 Hosting Bundle on the server, then restart IIS.
2. `dotnet publish src/Api -c Release -o <site folder>`; this generates `web.config`.
3. Create a site with an application pool set to "No Managed Code".
4. Set configuration as environment variables on the site (not in the repo), for example `Mongo__ConnectionString`, `Mongo__Database`, later `Ai__DailyCapUsd`.
5. One origin for the browser: the public site uses URL Rewrite + Application Request Routing to send `/api/*` to this API site and everything else to the Next.js site, so httpOnly session cookies work. (The API routes get an `/api` prefix when the endpoints are built in P1+.)
6. MongoDB runs as its own service or a managed cluster; the API only needs its connection string.
7. Health check URL for monitoring: `/health/ready`.
