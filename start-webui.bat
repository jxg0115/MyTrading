@echo off
title MyTrading WebUI
cd /d D:\StockSharp\MyTrading\WebUI
echo 启动 MyTrading WebUI...  浏览器访问 http://localhost:5000
start "" "http://localhost:5000"
dotnet run -c Release --no-build
