'use strict'

import * as vscode from 'vscode'

import {MoonSharpDebugConfigurationProvider} from './moonSharpDebugConfiguration'

let masterPort : number = 41912
let masterDebugSession : vscode.DebugSession | null = null

type Session = {
	port: number,
	name: string,
}

function findSession(sessions: Session[], port: number) : Session | null {
	for (const session of sessions) {
		if (session.port === port) {
			return session
		}
	}

	return null
}

async function displaySessionPrompt(sessions: Session[]) {
	const titles : string[] = sessions.map(session => `${session.name} [${session.port}]`)
	const selectedNames : string[] = await vscode.window.showQuickPick(titles, {canPickMany: true}) || []
	const selectedPorts : number[] = selectedNames.map(title => {
		const match = title.match(/\[([0-9]+)\]$/)
		const port = match ? parseInt(match[1]) : 0
		return findSession(sessions, port) ? port : 0
	}).filter(p => p !== 0)

	let masterSelected = false

	if (masterDebugSession) {
		for (const port of selectedPorts) {
			const session = findSession(sessions, port)
			vscode.debug.startDebugging(masterDebugSession.workspaceFolder, {
				...masterDebugSession.configuration,
				name: session ? session.name : port.toString(),
				debugServer: port,
				slave: true,
			})

			if (port === masterPort) {
				masterSelected = true
			}
		}

		if (!masterSelected) {
			masterDebugSession.customRequest('disconnect')
		}
	}
}

async function selectSessions(debugSession: vscode.DebugSession) {
	vscode.window.withProgress({
		location: vscode.ProgressLocation.Window,
		title: 'Fetching peer sessions',
		cancellable: false
	 }, async () => {
		const sessionResponse = await debugSession.customRequest('_sessions')
		const sessions : Session[] = sessionResponse.sessions

		if (sessions.length > 0) {
			await displaySessionPrompt(sessions)
		}
	 })
}

function onDidStartDebugSession(debugSession: vscode.DebugSession) {
	if (debugSession.configuration.debugServer === masterPort) {
		masterDebugSession = debugSession
		selectSessions(debugSession)
	}
}

function onDidTerminateDebugSession(debugSession: vscode.DebugSession) {
	if (debugSession.configuration.debugServer === masterPort) {
		masterDebugSession = null
	}
}

export function activate(context: vscode.ExtensionContext) {
	context.subscriptions.push(vscode.debug.registerDebugConfigurationProvider('moonsharp-lua', new MoonSharpDebugConfigurationProvider(port => masterPort = port)));
	vscode.debug.onDidStartDebugSession(onDidStartDebugSession)
	vscode.debug.onDidTerminateDebugSession(onDidTerminateDebugSession)
}

export function deactivate() {
}
