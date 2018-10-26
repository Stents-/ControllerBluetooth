# Controller Bluetooth
This is the bluetooth code from the Aether project 2018. This code managed all the communication between the bluetooth controller and the hololens. It is split up into two projects. It includes a basic packet format for sending and recieving data.

## ControllerTeensy

This folder contains the code that ran directly on the controller. Its primary job is to collect the input from the connected devices and package that data into a packet that gets transmitted over the serial bluetooth connection.

## GloveController

This folder contains the code that ran on the Hololens. This file would compile to a DLL that gets included in the unity project. Its role is to recieve and decode the packets sent by the controller and also to provide an interface for the unity project to use this data.