# Step 1

Step 2 replaces this local registration flow. The current setup is described in the repository README.



Startup flow:

1. The app launches.
2. It checks local device registration.
3. An unregistered device sees the registration screen.
4. A registered device sees the registered-device screen and a placeholder login form.

Registration collects the device name, branch name, historical branch number (`POSNumber`), and device type. The client generates `DeviceID` and `RegisteredAt`, then stores the record in browser local storage so it survives reload.

The API serves `GET /health` only.
