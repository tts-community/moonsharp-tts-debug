'use strict'

import * as vscode from 'vscode'

import {MoonSharpDebugConfigurationProvider} from './moonSharpDebugConfiguration'

let masterPort : number = 41912
let masterDebugSession : vscode.DebugSession | null = null

type Session = {
	port: number,
	name: string,
}

type SessionQuickPickItem = vscode.QuickPickItem & {
	session: Session
}

function showSessionPrompt(sessions: Session[]) : Promise<Session[]> {
	return new Promise((resolve: (value?: Session[] | PromiseLike<Session[]>) => void, reject: (reason?: any) => void) => {
		let accepted = false
		const quickPick : vscode.QuickPick<SessionQuickPickItem> = vscode.window.createQuickPick()
		quickPick.canSelectMany = true
		quickPick.items = sessions.map(session => ({
			label: session.name,
			session: session,
		}))
		quickPick.selectedItems = quickPick.items
		quickPick.onDidAccept(() => {
			resolve(quickPick.selectedItems.map(item => item.session))
			accepted = true
			quickPick.hide()
		})
		quickPick.onDidHide(() => !accepted && resolve([]))
		quickPick.show()
	})
}

async function chooseSessions(sessions: Session[]) {
	const selectedSessions : Session[] = await showSessionPrompt(sessions)

	if (masterDebugSession) {
		let masterSelected = false

		for (const session of selectedSessions) {
			if (session.port === masterPort) {
				masterSelected = true
			} else {
				vscode.debug.startDebugging(masterDebugSession.workspaceFolder, {
					...masterDebugSession.configuration,
					name: session.name,
					debugServer: session.port,
					slave: true,
				})
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
			await chooseSessions(sessions)
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
